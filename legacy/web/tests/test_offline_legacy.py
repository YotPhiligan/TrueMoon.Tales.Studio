"""M4 offline namespace regressions. All Book/framework names below are TEMP fixtures."""
import builtins
import copy
from contextlib import contextmanager, ExitStack
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from unittest.mock import patch

from fixtures import Fixture
from studio.jobs import Jobs
from studio.model import Store, StudioError, atomic, digest, read
from studio.paths import WorkspacePaths, PathPolicyError, check_export_path


@contextmanager
def forbid_legacy_io(root):
    """Fail at the first Python resolve/stat/open of the old lexical namespace."""
    root = Path(os.path.abspath(root))
    attempts = []

    def guard(value, operation):
        if isinstance(value, (str, bytes, os.PathLike)):
            path = Path(os.path.abspath(os.fsdecode(value)))
            if path == root or path.is_relative_to(root):
                attempts.append((operation, str(path)))
                raise AssertionError('Old namespace IO: ' + operation + ' ' + str(path))

    real_resolve, real_stat = Path.resolve, os.stat
    real_path_open, real_open, real_io_open = Path.open, builtins.open, io.open

    def resolve(path, *args, **kwargs):
        guard(path, 'resolve');return real_resolve(path, *args, **kwargs)

    def stat(path, *args, **kwargs):
        guard(path, 'stat');return real_stat(path, *args, **kwargs)

    def path_open(path, *args, **kwargs):
        guard(path, 'Path.open');return real_path_open(path, *args, **kwargs)

    def file_open(file, *args, **kwargs):
        guard(file, 'open');return real_open(file, *args, **kwargs)

    def io_open(file, *args, **kwargs):
        guard(file, 'io.open');return real_io_open(file, *args, **kwargs)

    with ExitStack() as stack:
        for target, name, replacement in [(Path, 'resolve', resolve), (os, 'stat', stat),
                (Path, 'open', path_open), (builtins, 'open', file_open), (io, 'open', io_open)]:
            stack.enter_context(patch.object(target, name, replacement))
        yield attempts


class OfflineLegacyTests(Fixture):
    def setUp(self):
        super().setUp()
        self.new_book = self.base / 'Videos'
        self.new_amv = self.new_book / 'TrueMoonTales/AMV'
        self.new_projects = self.new_amv / 'Projects'
        self.new_state = self.base / 'NewState'
        self.new_arts = self.base / 'Arts'
        self.code = self.base / 'Studio'
        self.shared = self.base / 'Shared'
        self.framework = self.shared / 'TrueMoon'
        self.cache = self.base / 'Cache';self.exports = self.base / 'Exports'
        for folder in [self.new_projects, self.new_state, self.new_arts, self.code,
                       self.framework, self.cache, self.exports]:
            folder.mkdir(parents=True, exist_ok=True)
        self.framework_marker = self.framework / 'parallel.cs'
        self.framework_marker.write_bytes(b'fixture framework: never change')

    def policy(self, **changes):
        args = dict(code_root=self.code, projects_root=self.new_projects,
                    mounts={'videos': self.new_book, 'arts': self.new_arts, 'shared': self.shared},
                    protected_roots=[self.framework], profiles_root=self.core,
                    cache_root=self.cache, exports_root=self.exports,
                    python_executable=sys.executable, comfy_root=self.base / 'FakeComfy',
                    legacy_read_enabled=False)
        args.update(changes)
        return WorkspacePaths(self.book, self.new_amv, self.new_state, **args)

    def aliases(self):
        return [{'from': str(self.book), 'to': '@videos'},
                {'from': str(self.book / 'Assets'), 'to': '@arts'}]

    def migrated(self):
        shutil.copytree(self.book, self.new_book, dirs_exist_ok=True)
        shutil.copytree(self.book / 'Assets', self.new_arts, dirs_exist_ok=True)
        incoming = self.new_state / 'Incoming.json'
        shutil.copyfile(self.store.registry_path, incoming)
        with forbid_legacy_io(self.book) as attempts:
            policy = self.policy(aliases=self.aliases())
            store = Store(self.book, self.new_amv, self.new_state, paths=policy)
            store.adopt_registry(incoming)
            self.assertEqual([], attempts)
        return store

    @staticmethod
    def tree(folder):
        return {str(p.relative_to(folder)): digest(p) for p in folder.rglob('*') if p.is_file()}

    def denied(self, fn, status=403, kind=PathPolicyError):
        with self.assertRaises(kind) as caught:fn()
        self.assertEqual(status, caught.exception.status)

    def test_flag_defaults_to_legacy_compatibility_and_rejects_non_boolean_values(self):
        enabled = self.policy(legacy_read_enabled=True)
        self.assertTrue(enabled.legacy_read_enabled)
        self.assertEqual(self.images[0], enabled.resolve('Assets/ref-1.png'))
        default = WorkspacePaths(self.book, self.new_amv, self.new_state,
                                 projects_root=self.new_projects)
        self.assertTrue(default.legacy_read_enabled)
        self.assertEqual(self.images[0], default.resolve(self.images[0]))
        for value in [0, 1, 'false', None, [], {}]:
            with self.subTest(value=value):
                self.denied(lambda: self.policy(legacy_read_enabled=value), 422)
        self.denied(lambda: self.policy(legacy_mode=True), 422)

    def test_config_offline_load_is_lexical_and_flag_schema_is_strict(self):
        source = self.new_arts / 'ref-1.png';source.write_bytes(b'migrated')
        config = self.code / 'workspace.json'
        data = dict(version=1, legacy_root='../Book', amv_root=str(self.new_amv),
                    state_root=str(self.new_state), projects_root=str(self.new_projects),
                    mounts={'videos': str(self.new_book), 'arts': str(self.new_arts)},
                    profiles_root=str(self.core), python_executable=sys.executable,
                    legacy_read_enabled=False,
                    path_aliases=[{'from': str(self.book / 'Assets'), 'to': '@arts'}])
        config.write_text(json.dumps(data), encoding='utf-8')
        with forbid_legacy_io(self.book) as attempts:
            loaded = WorkspacePaths.load(config, code_root=self.code)
            self.assertFalse(loaded.legacy_read_enabled)
            self.assertEqual(source, loaded.resolve('Assets/ref-1.png'))
            self.assertEqual([], attempts)
        for value in [0, 1, 'false', None, [], {}]:
            with self.subTest(value=value):
                config.write_text(json.dumps({**data, 'legacy_read_enabled': value}), encoding='utf-8')
                self.denied(lambda: WorkspacePaths.load(config), 422)
        for key in ['amv_root', 'state_root', 'projects_root', 'profiles_root', 'mounts', 'project_write_roots']:
            bad = {'unsafe': str(self.book)} if key == 'mounts' else [str(self.book)] if key == 'project_write_roots' else str(self.book)
            config.write_text(json.dumps({**data, key: bad}), encoding='utf-8')
            with self.subTest(old_physical_root=key), forbid_legacy_io(self.book) as attempts:
                self.denied(lambda: WorkspacePaths.load(config), 422)
                self.assertEqual([], attempts)
        data.pop('legacy_read_enabled');config.write_text(json.dumps(data), encoding='utf-8')
        self.assertTrue(WorkspacePaths.load(config).legacy_read_enabled)

    def test_aliases_resolve_absolute_relative_and_longest_prefix_without_old_io(self):
        migrated = self.new_arts / self.images[0].name;migrated.write_bytes(b'NEW bytes')
        generic = self.new_book / 'Assets' / self.images[0].name
        generic.parent.mkdir(exist_ok=True);generic.write_bytes(b'wrong broad alias')
        original = self.images[0].read_bytes()
        with forbid_legacy_io(self.book) as attempts:
            policy = self.policy(aliases=self.aliases())
            for value in [self.images[0], 'Assets/ref-1.png', str(self.book / 'Assets/../Assets/ref-1.png')]:
                self.assertEqual(migrated, policy.resolve(value))
                self.assertEqual(b'NEW bytes', policy.resolve(value).read_bytes())
                self.assertEqual('@arts/ref-1.png', policy.reference(value))
            self.denied(lambda: policy.resolve(self.images[0], write=True))
            self.assertNotIn(str(self.book), policy.export_policy()['read_roots'])
            self.assertNotIn(str(self.book), policy.export_policy()['write_roots'])
            request = {'path_policy': policy.export_policy()}
            self.assertEqual(migrated, check_export_path(request, migrated))
            self.denied(lambda: policy.resolve(self.framework_marker))
            self.assertEqual([], attempts)
        self.assertEqual(original, self.images[0].read_bytes())

    def test_unmapped_existing_old_paths_deny_even_exists_false_without_old_io(self):
        with forbid_legacy_io(self.book) as attempts:
            policy = self.policy(aliases=[{'from': str(self.book / 'Other'), 'to': '@arts'}])
            for value in [self.images[0], 'Assets/ref-1.png', self.book, 'Assets/../../Book/Assets/ref-1.png']:
                for exists in [False, True]:
                    for write in [False, True]:
                        with self.subTest(value=str(value), exists=exists, write=write):
                            self.denied(lambda: policy.resolve(value, exists=exists, write=write))
            self.assertEqual([], attempts)

    def test_alias_cycle_growth_and_protected_targets_fail_closed_without_old_io(self):
        for aliases in [
            [{'from': str(self.book / 'A'), 'to': str(self.book / 'B')},
             {'from': str(self.book / 'B'), 'to': str(self.book / 'A')}],
            [{'from': str(self.book / 'A'), 'to': str(self.book / 'A/grows')}],
        ]:
            with self.subTest(aliases=aliases), forbid_legacy_io(self.book) as attempts:
                policy = self.policy(aliases=aliases)
                self.denied(lambda: policy.resolve('A/ref.png', exists=False), 422)
                self.assertEqual([], attempts)
        with forbid_legacy_io(self.book) as attempts:
            self.denied(lambda: self.policy(aliases=[{'from': str(self.book / 'A'), 'to': '@shared/TrueMoon'}]))
            self.assertEqual([], attempts)

    def test_offline_mount_and_write_overlap_and_unsafe_junction_are_denied(self):
        for location in [self.book, self.book / 'Assets', self.base]:
            with self.subTest(mount=location), forbid_legacy_io(self.book) as attempts:
                self.denied(lambda: self.policy(mounts={'unsafe': location}), 422)
                self.assertEqual([], attempts)
            with self.subTest(write=location), forbid_legacy_io(self.book) as attempts:
                self.denied(lambda: self.policy(project_write_roots=[location]), 422)
                self.assertEqual([], attempts)
        link = self.new_arts / 'escape'
        # Windows junctions require no administrator/developer-mode symlink privilege.
        result = subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(self.book / 'Assets')],
                                capture_output=True, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        diagnostics = (result.stdout + result.stderr).decode('utf-8', errors='replace')
        self.assertEqual(0, result.returncode, diagnostics)
        self.addCleanup(lambda: link.rmdir() if link.exists() else None)
        with forbid_legacy_io(self.book) as attempts:
            policy = self.policy(aliases=self.aliases())
            for value in ['@arts/escape/ref-1.png', link / 'ref-1.png']:
                with self.subTest(unsafe_target=value):
                    self.denied(lambda: policy.resolve(value))
            self.denied(lambda: self.policy(mounts={'unsafe': link}), 422)
            self.denied(lambda: self.policy(aliases=[{'from': str(self.book / 'A'), 'to': str(link)}]))
            self.assertEqual([], attempts)

    def test_registry_and_asset_aliases_preserve_graph_history_raw_ids_and_native_av(self):
        take, native = self.add_take();p = self.current();block = self.block(p)
        block['context_take_id'] = take['id'];block['selected_take_id'] = take['id']
        p['timeline'] = {'version': 1, 'fps': 24, 'segments': [{'id': 'stable-segment', 'start_frame': 12, 'duration_frames': 96}],
                         'clips': [{'id': 'stable-use', 'segment_id': 'stable-segment', 'block_id': self.bid,
                                    'take_id': take['id'], 'start_frame': 24, 'duration_frames': 34, 'source_in_frame': 2}],
                         'markers': [{'id': 'beat', 'frame': 71}], 'titles': [{'id': 'title-stable', 'text': 'Клэр'}],
                         'undo': [{'state': {'segments': [], 'clips': [], 'markers': []}, 'action': 'before'}], 'redo': []}
        # Cover both historical relative paths and historical absolute strings.
        aid = block['references'][0]['asset_id'];p['assets'][aid]['path'] = str(self.images[0])
        saved = self.store.commit(p, p['revision'], 'fixture', 'Before offline migration')
        project_bytes = self.store.location(self.pid).read_bytes();native_hash = digest(native)
        old_tree = self.tree(self.book);framework = self.tree(self.framework)
        store = self.migrated();destination = store.location(self.pid).parents[1]
        migrated_tree = self.tree(destination);registry = store.registry_path.read_bytes()
        with forbid_legacy_io(self.book) as attempts:
            self.assertEqual(saved, store.get(self.pid))
            self.assertEqual(project_bytes, store.location(self.pid).read_bytes())
            resolved_native = store.resolve_asset(saved, take['native_av_asset_id'])
            self.assertEqual(self.new_book / native.relative_to(self.book), resolved_native)
            self.assertEqual(native_hash, digest(resolved_native))
            existing = copy.deepcopy(saved['assets'][aid])
            self.assertEqual(aid, store.asset(saved, self.new_arts / self.images[0].name))
            self.assertEqual(existing, saved['assets'][aid])
            store.adopt_registry(self.new_state / 'Incoming.json')
            self.assertEqual(registry, store.registry_path.read_bytes())
            ambiguous = copy.deepcopy(saved)
            ambiguous['assets']['duplicate'] = {**existing, 'id': 'duplicate', 'path': '@arts/' + self.images[0].name}
            before = copy.deepcopy(ambiguous)
            self.denied(lambda: store.asset(ambiguous, self.new_arts / self.images[0].name), 409, StudioError)
            self.assertEqual(before, ambiguous)
            self.assertEqual([], attempts)
        self.assertEqual(migrated_tree, self.tree(destination));self.assertEqual(old_tree, self.tree(self.book))
        self.assertEqual(framework, self.tree(self.framework))

    def test_pre_migration_prepared_launch_rejects_before_source_io_intent_or_mutation(self):
        with patch.object(self.jobs, 'powershell', return_value='fake-pwsh'):
            old_job = self.prepared()
        store = self.migrated()
        jobs = Jobs(store, python=Path(sys.executable), core=self.core)
        before = self.tree(store.location(self.pid).parents[1]);record = copy.deepcopy(jobs.get(self.pid, old_job['id']))
        with forbid_legacy_io(self.book) as attempts, patch('studio.jobs.threading.Thread') as thread, \
                patch.object(jobs, 'command') as command, patch.object(jobs, 'worker') as worker:
            self.denied(lambda: jobs.launch(self.pid, old_job['id']), 409, StudioError)
            self.assertEqual(record, jobs.get(self.pid, old_job['id']))
            self.assertFalse((jobs.directory(self.pid, old_job['id']) / 'Launch.intent').exists())
            self.assertEqual({}, jobs.threads);thread.assert_not_called();command.assert_not_called();worker.assert_not_called()
            self.assertEqual([], attempts)
        self.assertEqual(before, self.tree(store.location(self.pid).parents[1]))

    def test_fresh_offline_motion_context_prepare_and_one_launch_use_only_new_physical_paths(self):
        take, native = self.add_take()
        self.save({'context_take_id': take['id'], 'settings': {**self.block()['settings'], 'mode': 'context'}})
        store = self.migrated();jobs = Jobs(store, python=Path(sys.executable), core=self.core)
        project_bytes = store.location(self.pid).read_bytes();before_framework = self.tree(self.framework)
        with forbid_legacy_io(self.book) as attempts, patch.object(jobs, 'powershell', return_value='fake-pwsh'), \
                patch.object(jobs, 'command', side_effect=self.fake_prepare) as command, \
                patch('studio.jobs.threading.Thread') as thread:
            job = jobs.prepare(self.pid, self.bid, store.get(self.pid)['revision'])
            config = read(job['config'])
            self.assertEqual('prepared', job['state']);self.assertFalse(job['launch_requested'])
            self.assertEqual('omni-context', config['profile']);self.assertEqual(2, config['clip_index'])
            self.assertEqual(str(self.new_book / native.relative_to(self.book)), config['previous_latent'])
            self.assertEqual([str(self.new_arts / image.name) for image in self.images], [r['path'] for r in config['references']])
            self.assertTrue(all(not store.paths.is_legacy_path(path) for path in job['input_files']))
            self.assertIn(config['previous_latent'], [r['path'] for r in job['source_files']])
            self.assertEqual(1, command.call_count)
            launched = jobs.launch(self.pid, job['id'])
            self.assertEqual('queued', launched['state']);self.assertTrue(launched['launch_requested'])
            self.assertTrue((jobs.directory(self.pid, job['id']) / 'Launch.intent').is_file())
            thread.assert_called_once_with(target=jobs.worker, args=(self.pid, job['id']), daemon=True)
            thread.return_value.start.assert_called_once_with()
            self.denied(lambda: jobs.launch(self.pid, job['id']), 409, StudioError)
            self.assertEqual(1, thread.call_count);self.assertEqual(1, command.call_count)
            self.assertEqual([], attempts)
        self.assertEqual(project_bytes, store.location(self.pid).read_bytes())
        self.assertEqual(before_framework, self.tree(self.framework))
