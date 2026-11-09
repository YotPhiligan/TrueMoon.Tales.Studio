import copy
import fractions
import json
from pathlib import Path
import shutil
import sys
from unittest.mock import patch

from scenario_fixtures import ScenarioFixture
from studio import agent, export_plan, library, titles, timeline
from studio.model import Store, StudioError, atomic, digest, read
from studio.paths import WorkspacePaths
from studio.export_worker import DraftRenderer
from studio.exports import Exports
from studio.paths import check_export_path


class WorkspaceIntegrationTests(ScenarioFixture):
    def setUp(self):
        super().setUp()
        self.old_store = self.store
        self.ext_projects = self.base / 'ExternalProjects';self.ext_arts = self.base / 'ExternalArts'
        self.ext_media = self.base / 'ExternalMedia';self.new_state = self.base / 'NewState'
        self.ext_cache = self.base / 'ExternalCache';self.ext_exports = self.base / 'ExternalExports'
        self.code = self.base / 'StudioCode';self.profiles = self.base / 'Profiles'
        self.framework = self.base / 'Framework/TrueMoon'
        for path in [self.ext_projects,self.ext_arts,self.ext_media,self.new_state,self.ext_cache,self.ext_exports,self.code,self.profiles,self.framework]:path.mkdir(parents=True,exist_ok=True)
        self.framework_marker = self.framework / 'parallel-work.cs';self.framework_marker.write_bytes(b'do not modify fixture framework')
        (self.code / 'studio_agent.py').write_text('# configured CLI, never executed')
        (self.code / 'AgentIntegration.md').write_text('Configured integration guide')
        self.aliases = []

    def configured(self, aliases=()):
        self.policy = WorkspacePaths(self.book,self.amv,self.new_state,code_root=self.code,projects_root=self.ext_projects,
            mounts={'projects':self.ext_projects,'arts':self.ext_arts,'media':self.ext_media,'old_state':self.old_store.state},
            project_write_roots=[self.amv/'Projects'],protected_roots=[self.framework],aliases=aliases,
            cache_root=self.ext_cache,exports_root=self.ext_exports,profiles_root=self.profiles,
            python_executable=sys.executable,comfy_root=self.base/'FakeComfy')
        return Store(self.book,self.amv,self.new_state,paths=self.policy)

    def tree(self, folder):
        return {str(p.relative_to(folder)):digest(p) for p in folder.rglob('*') if p.is_file()}

    def test_store_external_project_asset_serialization_and_read_only_mount_do_not_touch_framework(self):
        store = self.configured();before = self.tree(self.framework)
        p = store.create('External project',project_id='external')
        self.assertEqual('@projects/external',p['root']);self.assertEqual(self.ext_projects/'external/Studio.project.json',store.location(p['id']))
        image = self.ext_arts/'Claire.png';image.write_bytes(b'new external image')
        aid = store.asset(p,image)
        self.assertEqual('@arts/Claire.png',p['assets'][aid]['path']);self.assertEqual(image,store.resolve_asset(p,aid))
        with self.assertRaises(StudioError) as caught:store.writable(image)
        self.assertEqual(403,caught.exception.status)
        self.assertEqual(self.ext_cache/'external/Titles',store.cache_dir('external','Titles'))
        self.assertEqual(before,self.tree(self.framework));self.assert_no_generation()

    def test_project_json_can_move_to_root_without_moving_history_or_rewriting_graph(self):
        store = self.configured()
        p = store.create('Project to relocate')
        root = store.project_dir(p['id'])
        current = store.location(p['id'])
        legacy = store.production_dir(p['id']) / current.name
        legacy.parent.mkdir()
        current.rename(legacy)
        before = legacy.read_bytes()
        self.assertEqual(p, store.get(p['id']))
        legacy.rename(current)
        self.assertEqual(before, current.read_bytes())
        self.assertEqual(p, store.get(p['id']))
        store.commit(p, p['revision'], 'author', 'After relocation')
        self.assertEqual([before], [path.read_bytes() for path in (legacy.parent/'History').glob('*.json')])
        legacy.write_bytes(before)
        with self.assertRaises(StudioError) as caught:
            store.get(p['id'])
        self.assertEqual(409, caught.exception.status)

    def test_asset_catalog_discovers_local_media_and_reports_live_reverse_links_without_writes(self):
        p = self.old_store.get(self.pid)
        root = self.old_store.project_dir(self.pid)
        new = root / 'Frames/retakes/Ручная опора.png'
        new.parent.mkdir(parents=True)
        new.write_bytes(b'asset fixture')
        (new.parent/'internal.json').write_bytes(b'not a media file')
        frame = root / 'Frames/registered.png'
        frame.write_bytes(b'asset fixture')
        aid = self.old_store.asset(p, frame)
        block = self.old_store.find_block(p, self.bid)
        block['references'].append({'id':'registered-ref','asset_id':aid,'role':'Start'})
        block['guides'].append({'reference_id':'registered-ref','frame':0})
        p = self.old_store.commit(p, p['revision'], 'fixture', 'Catalog data')
        before = self.old_store.location(self.pid).read_bytes()
        rows = {row['path']:row for row in library.catalog(self.old_store, self.pid)['assets']}
        self.assertNotIn('Frames/retakes/internal.json', rows)
        self.assertIsNone(rows['Frames/retakes/Ручная опора.png']['asset_id'])
        self.assertEqual(aid, rows['Frames/registered.png']['asset_id'])
        self.assertEqual('guide', rows['Frames/registered.png']['uses'][0]['role'])
        self.assertEqual([0], rows['Frames/registered.png']['uses'][0]['frames'])
        video = next(row for row in rows.values() if row['asset_id'] == self.take['video_asset_id'])
        self.assertEqual({'take','montage_video'}, {use['role'] for use in video['uses']})
        self.assertEqual(new, library.preview_path(self.old_store, self.pid, 'Frames/retakes/Ручная опора.png'))
        for name in ['Frames/../registered.png', 'Frames/retakes/internal.json', 'Production/saved.png', 'Frames/retakes/image.png:stream']:
            with self.subTest(name=name), self.assertRaises(StudioError):
                library.preview_path(self.old_store, self.pid, name)
        self.assertEqual(before, self.old_store.location(self.pid).read_bytes())

    def test_picker_assigns_image_and_guide_once_and_reuses_the_asset(self):
        store = self.old_store
        root = store.project_dir(self.pid)
        picture = root / 'References/Picker.png'
        picture.parent.mkdir(exist_ok=True)
        picture.write_bytes(b'fixture')
        p = store.get(self.pid)
        block = store.find_block(p, self.bid)
        count = len(block['references'])
        # The fixture already uses frame 0. Choose an unused intermediate index.
        p = store.operation(self.pid, p['revision'], 'assign_catalog_image',
                            {'block_id':self.bid,'asset_selection':{'file':'References/Picker.png'}})
        ref = store.find_block(p,self.bid)['references'][-1]
        p = store.operation(self.pid, p['revision'], 'assign_catalog_image',
                            {'block_id':self.bid,'asset_selection':{'asset_id':ref['asset_id']},'guide_frame':3})
        block = store.find_block(p,self.bid)
        self.assertEqual(count+1,len(block['references']))
        self.assertIn({'reference_id':ref['id'],'frame':3},block['guides'])
        self.assertEqual(p['revision'],store.operation(self.pid,p['revision'],'assign_catalog_image',
                         {'block_id':self.bid,'asset_selection':{'asset_id':ref['asset_id']},'guide_frame':3})['revision'])

    def test_picker_adds_video_and_audio_in_one_undoable_placement(self):
        store=self.old_store
        source=store.project_dir(self.pid)/'video/Picker.mp4'
        source.parent.mkdir(exist_ok=True)
        source.write_bytes(b'fixture')
        p=store.get(self.pid)
        before=store.location(self.pid).read_bytes()
        args={'segment_id':'s2','asset_selection':{'file':'video/Picker.mp4'},
              'source_in_frame':6,'duration_frames':24,'audio_stream':0}
        with patch.object(store,'probe_video',return_value={'seconds':2}), patch.object(store,'waveform_path',return_value={'seconds':.2}):
            with self.assertRaises(StudioError): library.add_video(store,p,args)
        self.assertEqual(before,store.location(self.pid).read_bytes())
        self.assertNotIn('Picker.mp4',[row['name'] for row in p['assets'].values()])
        with patch.object(store,'probe_video',return_value={'seconds':2}), patch.object(store,'waveform_path',return_value={'seconds':1}):
            changed=library.add_video(store,p,args)
        result=store.commit(changed,p['revision'],'author','Video and sound')
        event=result['timeline']['sound_events'][0]
        clip=next(c for c in result['timeline']['clips'] if c['id']==event['clip_id'])
        self.assertTrue(clip['mute_native_audio'])
        self.assertEqual((6,18), (event['source_in_frame'],event['duration_frames']))
        self.assertEqual('Picker.mp4',result['assets'][event['asset_id']]['name'])
        reverted=store.timeline_operation(self.pid,result['revision'],'undo',{})
        self.assertFalse(reverted['timeline']['sound_events'])
        self.assertNotIn(clip['id'],[c['id'] for c in reverted['timeline']['clips']])
        self.assertIn(event['asset_id'],reverted['assets'])

    def test_alias_registration_preserves_existing_id_raw_path_and_rejects_ambiguous_resolved_assets(self):
        old = self.images[0];new = self.ext_arts/old.name;shutil.copy2(old,new)
        store = self.configured([{'from':str(old.parent),'to':'@arts'}]);p = self.old_store.get(self.pid)
        original = next(a for a in p['assets'].values() if a['path']==old.relative_to(self.book).as_posix())
        before = copy.deepcopy(original);aid = store.asset(p,new)
        self.assertEqual(before['id'],aid);self.assertEqual(before['path'],p['assets'][aid]['path'])
        self.assertEqual(new,store.resolve_asset(p,aid))
        p['assets']['duplicate']={**p['assets'][aid],'id':'duplicate','path':'@arts/'+new.name}
        graph = copy.deepcopy(p)
        with self.assertRaises(StudioError) as caught:store.asset(p,new)
        self.assertEqual(409,caught.exception.status);self.assertEqual(graph,p)
        self.assertEqual(self.images[0].read_bytes(),new.read_bytes());self.assert_no_generation()

    def test_adopt_alias_project_registry_preserves_exact_graph_native_history_titles_timing_and_ids(self):
        p = self.old_store.get(self.pid);block = self.old_store.find_block(p,self.bid)
        block['context_take_id']=self.take['id']
        p['timeline']['titles']=[titles.new_row({'text':'Сказки истинной луны','font_size':20,'start_frame':168,'duration_frames':48},identifier='title-stable')]
        p['timeline']['transitions']=[{'left_clip_id':'c1','right_clip_id':'c2','fade_out_frames':4,'pause_frames':16,'fade_in_frames':4}]
        p['timeline']['undo']=[{'state':timeline.content(self.seed['timeline']),'action':'fixture-original-history'}]
        self.old_store.commit(p,p['revision'],'fixture','Rich graph before adoption')
        p = self.old_store.get(self.pid);source_root=self.old_store.location(self.pid).parent.parent
        destination=self.ext_projects/'preserved-project';shutil.copytree(source_root,destination)
        old_graph=(source_root/'Production/Studio.project.json').read_bytes();before=self.tree(destination)
        source_hashes=self.source_state();framework=self.tree(self.framework)
        store=self.configured([{'from':str(source_root),'to':str(destination)}])
        rows=store.adopt_registry(self.old_store.registry_path)
        self.assertEqual([self.pid],[r['id'] for r in rows]);self.assertEqual(p,store.get(self.pid))
        self.assertEqual(destination/'Production/Studio.project.json',store.location(self.pid))
        self.assertEqual(old_graph,store.location(self.pid).read_bytes());self.assertEqual(before,self.tree(destination))
        registry_bytes=store.registry_path.read_bytes();store.adopt_registry(self.old_store.registry_path)
        self.assertEqual(registry_bytes,store.registry_path.read_bytes());self.assertEqual(before,self.tree(destination))
        self.assertEqual(source_hashes,self.source_state());self.assertEqual(framework,self.tree(self.framework));self.assert_no_generation()

    def test_registry_invalid_second_graph_and_existing_different_registry_are_atomic(self):
        store=self.configured();bad=self.base/'bad-project';bad.mkdir()
        # New registry is configured-read-only state input; all project roots remain permitted.
        second=self.ext_projects/'wrong';(second/'Production').mkdir(parents=True)
        atomic(second/'Production/Studio.project.json',{**self.current(),'id':'mismatched','root':'@projects/wrong'})
        candidate={'projects':copy.deepcopy(self.old_store.registry['projects'])+[{'id':'second','root':'@projects/wrong'}]}
        incoming=self.old_store.state/'incoming.json';atomic(incoming,candidate)
        before=self.tree(self.book)
        with self.assertRaises(StudioError) as caught:store.adopt_registry(incoming)
        self.assertEqual(409,caught.exception.status);self.assertFalse(store.registry_path.exists());self.assertEqual({'projects':[]},store.registry)
        self.assertEqual(before,self.tree(self.book))
        store.adopt_registry(self.old_store.registry_path);saved=store.registry_path.read_bytes()
        empty=self.old_store.state/'empty.json';atomic(empty,{'projects':[]})
        with self.assertRaises(StudioError) as caught:store.adopt_registry(empty)
        self.assertEqual(409,caught.exception.status);self.assertEqual(saved,store.registry_path.read_bytes());self.assert_no_generation()

    def test_agent_context_uses_configured_code_root_and_external_project_documents_read_only(self):
        source=self.old_store.location(self.pid).parent.parent;destination=self.ext_projects/'project-docs';shutil.copytree(source,destination)
        (destination/'AGENTS.md').write_text('External project instructions',encoding='utf-8')
        card=destination/'Shots/Card.md';card.parent.mkdir();card.write_text('External scene card',encoding='utf-8')
        p=read(destination/'Production/Studio.project.json');self.old_store.find_block(p,self.bid)['card_path']=str(card)
        atomic(destination/'Production/Studio.project.json',p)
        store=self.configured([{'from':str(source),'to':str(destination)}]);store.adopt_registry(self.old_store.registry_path)
        before=self.tree(destination);context=agent.context(store,self.pid,self.bid)
        self.assertEqual(str(self.code/'studio_agent.py'),context['integration']['cli_path'])
        self.assertEqual(str(self.code/'AgentIntegration.md'),context['integration']['guide_path'])
        docs={d['absolute_path']:d for d in context['documents']}
        self.assertEqual('External project instructions',docs[str(destination/'AGENTS.md')]['text'])
        self.assertEqual('External scene card',docs[str(card)]['text']);self.assertTrue(docs[str(card)]['path'].startswith('@projects/'))
        self.assertEqual(before,self.tree(destination));self.assert_no_generation()

    def test_frozen_policy_allows_external_assets_cache_and_cpu_export_but_denies_unknown_protected_and_readonly_output(self):
        import av
        import numpy as np
        from audio_fixtures import write_wav
        store=self.configured();p=store.create('External CPU',project_id='cpu')
        group=p['segments'][0]['id'];p=store.operation(p['id'],p['revision'],'add_block',{'segment_id':group,'label':'EXT-CPU'})
        bid=p['blocks'][0]['id'];video=self.ext_media/'colors.mp4';palette=[(255,0,0),(0,255,0),(0,0,255),(240,240,240)]
        with av.open(str(video),'w') as out:
            stream=out.add_stream('libx264',rate=8);stream.width=32;stream.height=32;stream.pix_fmt='yuv420p';stream.options={'crf':'0'}
            for index,color in enumerate(palette):
                pixels=np.zeros((32,32,3),np.uint8);pixels[:]=color
                frame=av.VideoFrame.from_ndarray(pixels,format='rgb24');frame.pts=index;frame.time_base=fractions.Fraction(1,8)
                for packet in stream.encode(frame):out.mux(packet)
            for packet in stream.encode(None):out.mux(packet)
        music=self.ext_media/'music.wav';sample_rate=16000
        values=(.1*np.sin(2*np.pi*440*np.arange(sample_rate)/sample_rate)*32767).astype(np.int16)
        write_wav(music,[(int(v),) for v in values])
        vid=store.asset(p,video);aid=store.asset(p,music);p['assets'][aid]['audio_info']={'seconds':1}
        store.find_block(p,bid)['takes']=[{'id':'external-take','video_asset_id':vid,'video_info':{'seconds':.5,'fps':8,'frames':4}}]
        p['fps']=8;p['output_size']=[160,90]
        title=titles.new_row({'text':'Клэр','font_size':16,'position':'upper','duration_frames':8,'fade_in_frames':0,'fade_out_frames':0},fps=8,identifier='external-title')
        p['timeline']={'version':1,'fps':8,'segments':[{'id':'s','name':'External','color':'mint','start_frame':0,'duration_frames':8}],
            'clips':[{'id':'c','block_id':bid,'take_id':'external-take','segment_id':'s','start_frame':0,'duration_frames':8,'source_in_frame':0,'source_out_frame':4,'speed':.5}],
            'music':{'id':'m','asset_id':aid,'start_frame':0,'duration_frames':8,'source_in_frame':0,'gain':1,'muted':False},
            'markers':[],'titles':[title],'clip_audio':False,'undo':[],'redo':[]}
        store.commit(p,p['revision'],'fixture','Ready external export');p=store.get(p['id'])
        before=store.location(p['id']).read_bytes();framework=self.tree(self.framework);legacy_book=self.tree(self.book);hashes={video:digest(video),music:digest(music)}
        request=export_plan.plan(store,p['id'],p['revision'],'project')
        self.assertEqual(self.policy.export_policy(),request['path_policy'])
        layer=request['assets'][request['title_layers'][0]['asset_id']]
        self.assertTrue(Path(layer['path']).is_relative_to(self.ext_cache))
        folder=Exports(store).folder(p['id'],'export-0000000000000001',create=True)
        self.assertEqual(self.ext_exports/'cpu/export-0000000000000001',folder);folder.mkdir(parents=True)
        request['timeout_seconds']=15;atomic(folder/'Request.json',request)
        result=DraftRenderer(folder).run();self.assertEqual((8,8,1),tuple(result['video_info'][k] for k in ['frames','fps','seconds']))
        self.assertTrue(result['video_info']['audio'])
        with av.open(str(folder/'Draft.mp4')) as out:frames=[f.to_ndarray(format='rgb24') for f in out.decode(video=0)]
        for index,frame in enumerate(frames):self.assertLess(float(np.max(np.abs(frame[45,80].astype(float)-palette[index//2]))),20)
        self.assertEqual(before,store.location(p['id']).read_bytes());self.assertEqual(hashes,{path:digest(path) for path in hashes});self.assertEqual(framework,self.tree(self.framework))
        self.assertEqual(legacy_book,self.tree(self.book))
        for bad,write in [(self.ext_arts/'denied.mp4',True),(self.base/'Unknown/file.mp4',False),(self.framework_marker,False),(self.framework/'out.mp4',True)]:
            with self.subTest(path=bad,write=write),self.assertRaises(ValueError):check_export_path(request,bad,write)
        broad=copy.deepcopy(request);broad['path_policy']['read_roots']=[str(self.base)]
        with self.assertRaises(ValueError):check_export_path(broad,self.framework_marker)
        legacy=copy.deepcopy(request);legacy.pop('path_policy')
        with self.assertRaises(ValueError):check_export_path(legacy,video)
        self.assert_no_generation()
