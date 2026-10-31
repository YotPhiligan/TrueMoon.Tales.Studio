"""Configured data roots and legacy path compatibility. No project rewrites."""
from pathlib import Path
import json,os,re

class PathPolicyError(Exception):
    def __init__(self,message,status=403):super().__init__(message);self.status=status

class WorkspacePaths:
    def __init__(self,legacy_root,amv_root,state_root,*,code_root=None,projects_root=None,mounts=None,
                 project_write_roots=(),protected_roots=(),aliases=(),cache_root=None,exports_root=None,
                 profiles_root=None,python_executable=None,comfy_root=None,legacy_mode=False,legacy_read_enabled=True):
        if type(legacy_read_enabled) is not bool or (legacy_mode and not legacy_read_enabled):raise PathPolicyError('Некорректный режим чтения legacy.',422)
        self.legacy_read_enabled=legacy_read_enabled
        self.legacy_root=Path(legacy_root).resolve() if legacy_read_enabled else Path(os.path.abspath(legacy_root))
        def physical(value):
            p=Path(os.path.abspath(value))
            if not legacy_read_enabled and (p==self.legacy_root or p.is_relative_to(self.legacy_root) or self.legacy_root.is_relative_to(p)):raise PathPolicyError('Физический каталог пересекается с отключённым legacy.',422)
            result=p.resolve() if legacy_read_enabled else Path(os.path.realpath(p))
            if not legacy_read_enabled and self.is_legacy_path(result):raise PathPolicyError('Физический каталог ведёт в отключённый legacy.',422)
            return result
        self.amv_root=physical(amv_root);self.state_root=physical(state_root)
        self.code_root=physical(code_root or self.amv_root/'UI');self.projects_root=physical(projects_root or self.amv_root/'Projects')
        self.cache_root=physical(cache_root) if cache_root else None;self.exports_root=physical(exports_root) if exports_root else None
        self.profiles_root=physical(profiles_root or self.amv_root/'Workflows/Omni.v1.1')
        self.python_executable=physical(python_executable or 'E:/ComfyUI_windows_portable/python_embeded/python.exe')
        self.comfy_root=physical(comfy_root or 'E:/ComfyUI_windows_portable/ComfyUI');self.legacy_mode=legacy_mode
        self.mounts={name:physical(value) for name,value in (mounts or {}).items()}
        if any(not isinstance(name,str) or not re.fullmatch(r'[a-z][a-z0-9_-]{0,31}',name) for name in self.mounts):raise PathPolicyError('Некорректное имя каталога конфигурации.',422)
        for name,value in {'projects':self.projects_root,'state':self.state_root,'studio':self.code_root,'profiles':self.profiles_root,**({'cache':self.cache_root} if self.cache_root else {}),**({'exports':self.exports_root} if self.exports_root else {})}.items():
            if name in self.mounts and self.mounts[name]!=value:raise PathPolicyError('Повторное имя системного каталога: '+name,422)
            self.mounts[name]=value
        self.protected_roots=tuple(Path(p).resolve() for p in protected_roots)
        self.read_roots=tuple(dict.fromkeys([*([self.legacy_root] if legacy_read_enabled else []),*self.mounts.values()]))
        self.write_roots=tuple(dict.fromkeys([self.legacy_root] if legacy_mode else [self.projects_root,self.state_root,*([self.cache_root] if self.cache_root else []),*([self.exports_root] if self.exports_root else []),*(physical(p) for p in project_write_roots)]))
        if legacy_mode:self.write_roots=tuple(dict.fromkeys([*self.write_roots,self.state_root]))
        if not legacy_read_enabled and any(p==self.legacy_root or p.is_relative_to(self.legacy_root) or self.legacy_root.is_relative_to(p) for p in [*self.read_roots,*self.write_roots]):raise PathPolicyError('Legacy отключён: реальные корни не должны пересекаться с его пространством имён.',422)
        if not legacy_mode:
            if any(p==protected or protected.is_relative_to(p) or p.is_relative_to(protected) for p in self.write_roots for protected in self.protected_roots):raise PathPolicyError('Каталог записи пересекается с защищённым проектом.',422)
        self.aliases=[]
        for item in aliases:
            if not isinstance(item,dict) or set(item)!={'from','to'}:raise PathPolicyError('Некорректная запись path_aliases.',422)
            source=self._absolute(item['from']);target=self._absolute(item['to'])
            for path in [source,target]:
                if not legacy_read_enabled and self.is_legacy_path(path):self._deny_protected(path)
                else:self._allowed(path,False)
            if source==target:raise PathPolicyError('Псевдоним пути ссылается на себя.',422)
            self.aliases.append((source,target))
        self.aliases.sort(key=lambda pair:len(pair[0].parts),reverse=True)

    @classmethod
    def legacy(cls,book,amv,state):return cls(book,amv,state,legacy_mode=True)

    @classmethod
    def load(cls,filename,*,code_root=None):
        config=Path(filename).resolve()
        try:data=json.loads(config.read_text(encoding='utf-8-sig'))
        except (OSError,ValueError) as e:raise PathPolicyError('Не удалось прочитать конфигурацию Studio: '+str(e),422) from e
        required={'version','legacy_root','amv_root','state_root','projects_root','mounts'}
        optional={'cache_root','exports_root','profiles_root','python_executable','comfy_root','project_write_roots','protected_roots','path_aliases','legacy_read_enabled'}
        if not isinstance(data,dict) or type(data.get('version')) is not int or data.get('version')!=1 or not required<=data.keys() or set(data)-required-optional:raise PathPolicyError('Некорректная схема конфигурации Studio v1.',422)
        enabled=data.get('legacy_read_enabled',True)
        if type(enabled) is not bool:raise PathPolicyError('legacy_read_enabled должен быть boolean.',422)
        legacy_namespace=None
        def path(value,logical=False):
            if not isinstance(value,str) or not value.strip():raise PathPolicyError('Ожидается непустой путь в конфигурации.',422)
            p=Path(os.path.expandvars(os.path.expanduser(value)));p=config.parent/p if not p.is_absolute() else p
            p=Path(os.path.abspath(p))
            if not logical and not enabled and legacy_namespace is not None and (p==legacy_namespace or p.is_relative_to(legacy_namespace) or legacy_namespace.is_relative_to(p)):raise PathPolicyError('Физический каталог пересекается с отключённым legacy.',422)
            result=p if logical else p.resolve() if enabled else Path(os.path.realpath(p))
            if not logical and not enabled and legacy_namespace is not None and (result==legacy_namespace or result.is_relative_to(legacy_namespace)):raise PathPolicyError('Физический каталог ведёт в отключённый legacy.',422)
            return result
        if not isinstance(data['mounts'],dict):raise PathPolicyError('mounts должен быть объектом.',422)
        for name in ['project_write_roots','protected_roots','path_aliases']:
            if name in data and not isinstance(data[name],list):raise PathPolicyError(name+' должен быть массивом.',422)
        legacy_namespace=path(data['legacy_root'],logical=not enabled)
        kwargs={'legacy_root':legacy_namespace,**{key:path(data[key]) for key in ['amv_root','state_root','projects_root']}}
        for key in ['cache_root','exports_root','profiles_root','python_executable','comfy_root']:
            if data.get(key) is not None:kwargs[key]=path(data[key])
        # The concurrently developed framework never becomes a Studio data root.
        code=Path(code_root or config.parent).resolve()
        protected=[code.parent/'TrueMoon',*(path(p) for p in data.get('protected_roots',[]))]
        return cls(**kwargs,legacy_read_enabled=enabled,code_root=code,mounts={k:path(v) for k,v in data['mounts'].items()},
                   project_write_roots=[path(p) for p in data.get('project_write_roots',[])],protected_roots=protected,aliases=data.get('path_aliases',[]))

    def _absolute(self,value):
        if not isinstance(value,(str,Path)) or not str(value).strip():raise PathPolicyError('Некорректный путь.',422)
        raw=str(value)
        if raw.startswith('@'):
            name,sep,tail=raw[1:].replace('\\','/').partition('/')
            if name not in self.mounts or '..' in Path(tail).parts or Path(tail).anchor:raise PathPolicyError('Неизвестный каталог или выход за его границы.',403)
            target=self._canonical(self.mounts[name]/tail)
            if not target.is_relative_to(self.mounts[name]):raise PathPolicyError('Выход за границы именованного каталога.',403)
            return target
        p=Path(value);p=Path(os.path.abspath(self.legacy_root/p if not p.is_absolute() else p))
        return p if not self.legacy_read_enabled and self.is_legacy_path(p) else self._canonical(p)

    def _canonical(self,path):
        result=Path(path).resolve() if self.legacy_read_enabled else Path(os.path.realpath(path))
        if not self.legacy_read_enabled and self.is_legacy_path(result):raise PathPolicyError('Физический путь ведёт в отключённый legacy.',403)
        return result

    def is_legacy_path(self,value):
        if str(value).startswith('@'):return False
        p=Path(value);p=Path(os.path.abspath(self.legacy_root/p if not p.is_absolute() else p))
        return p==self.legacy_root or p.is_relative_to(self.legacy_root)

    def _deny_protected(self,path):
        if any(path==p or path.is_relative_to(p) for p in self.protected_roots):raise PathPolicyError('Защищённый проект недоступен Studio.',403)

    def _allowed(self,path,write):
        self._deny_protected(path)
        roots=self.write_roots if write else self.read_roots
        if not any(path==p or path.is_relative_to(p) for p in roots):raise PathPolicyError('Путь вне разрешённых '+('каталогов записи.' if write else 'каталогов чтения.'),403)

    def resolve(self,value,exists=True,write=False):
        path=self._absolute(value);seen=set()
        for _ in range(16):
            if not self.legacy_read_enabled and self.is_legacy_path(path):self._deny_protected(path)
            else:self._allowed(path,False)
            if path in seen:raise PathPolicyError('Циклические псевдонимы путей.',422)
            seen.add(path);match=next(((a,b) for a,b in self.aliases if path==a or path.is_relative_to(a)),None)
            if match is None:break
            a,b=match;path=self._absolute(b/path.relative_to(a))
        else:raise PathPolicyError('Слишком длинная цепочка псевдонимов.',422)
        self._allowed(path,write)
        if exists and not path.is_file():raise PathPolicyError('Файл не найден: '+str(path),404)
        return path

    def reference(self,value):
        path=self.resolve(value,False)
        if path.is_relative_to(self.legacy_root):return path.relative_to(self.legacy_root).as_posix()
        matches=[(name,root) for name,root in self.mounts.items() if path==root or path.is_relative_to(root)]
        if not matches:raise PathPolicyError('Для пути не зарегистрирован именованный каталог.',403)
        name,root=max(matches,key=lambda pair:len(pair[1].parts));rel=path.relative_to(root).as_posix()
        return '@'+name+('/'+rel if rel!='.' else '')

    def export_policy(self):return {'read_roots':[str(p) for p in self.read_roots],'write_roots':[str(p) for p in self.write_roots],'protected_roots':[str(p) for p in self.protected_roots]}

def check_export_path(request,path,write=False):
    """Frozen policy for the independent CPU worker; legacy requests stay readable."""
    path=Path(path).resolve();policy=request.get('path_policy')
    if policy is None:policy={'read_roots':[request['book']],'write_roots':[request['book']],'protected_roots':[]}
    protected=[Path(p).resolve() for p in policy['protected_roots']]
    roots=[Path(p).resolve() for p in policy['write_roots' if write else 'read_roots']]
    if any(path==p or path.is_relative_to(p) for p in protected) or not any(path==p or path.is_relative_to(p) for p in roots):raise ValueError('Export path outside configured policy')
    return path
