import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from studio.paths import WorkspacePaths, PathPolicyError


class WorkspaceFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='studio-m2-tests-');self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.book = self.base / 'Book';self.amv = self.book / 'TrueMoonTales/AMV'
        self.code = self.base / 'StudioCode';self.projects = self.base / 'Data/Projects'
        self.arts = self.base / 'Data/Arts';self.portraits = self.arts / 'Characters';self.media = self.base / 'Media'
        self.state = self.base / 'State';self.cache = self.base / 'Derived/Cache';self.exports = self.base / 'Derived/Exports'
        self.profiles = self.base / 'Profiles';self.protected = self.base / 'Framework/TrueMoon';self.outside = self.base / 'Outside'
        for path in [self.amv, self.code, self.projects, self.portraits, self.media, self.state, self.cache, self.exports, self.profiles, self.protected, self.outside]:path.mkdir(parents=True, exist_ok=True)
        self.marker = self.protected / 'DO_NOT_TOUCH.txt';self.marker.write_bytes(b'parallel framework work fixture')
        self.mounts = {'projects': self.projects, 'arts': self.arts, 'portraits': self.portraits, 'media': self.media}

    def paths(self, **changes):
        args = {'code_root': self.code, 'projects_root': self.projects, 'mounts': self.mounts,
                'project_write_roots': [self.amv], 'protected_roots': [self.protected], 'cache_root': self.cache,
                'exports_root': self.exports, 'profiles_root': self.profiles, 'python_executable': sys.executable,
                'comfy_root': self.base / 'FakeComfy'}
        args.update(changes)
        return WorkspacePaths(self.book, self.amv, self.state, **args)

    def config(self, **changes):
        data = {'version': 1, 'legacy_root': str(self.book), 'amv_root': str(self.amv), 'state_root': str(self.state),
                'projects_root': str(self.projects), 'mounts': {k: str(v) for k, v in self.mounts.items()},
                'cache_root': str(self.cache), 'exports_root': str(self.exports), 'profiles_root': str(self.profiles),
                'project_write_roots': [str(self.amv)], 'protected_roots': [str(self.protected)],
                'python_executable': sys.executable, 'comfy_root': str(self.base / 'FakeComfy')}
        data.update(changes);path = self.code / 'workspace.json';path.write_text(json.dumps(data), encoding='utf-8-sig');return path

    def denied(self, operation, status=403):
        with self.assertRaises(PathPolicyError) as caught:operation()
        self.assertEqual(status, caught.exception.status)


class WorkspacePathTests(WorkspaceFixture):
    def test_config_relative_paths_public_roots_and_read_write_reference_roundtrips(self):
        config = self.config(legacy_root='../Book', amv_root='../Book/TrueMoonTales/AMV', state_root='../State')
        paths = WorkspacePaths.load(config)
        self.assertEqual((self.book, self.amv, self.code), (paths.legacy_root, paths.amv_root, paths.code_root))
        image = self.portraits / 'Claire.png';image.write_bytes(b'fixture image')
        legacy = self.book / 'notes.md';legacy.write_text('author notes', encoding='utf-8')
        self.assertEqual(image, paths.resolve('@arts/Characters/Claire.png'))
        self.assertEqual(image, paths.resolve('@portraits/Claire.png'))
        self.assertEqual('@portraits/Claire.png', paths.reference(image))
        self.assertEqual('notes.md', paths.reference(legacy))
        self.assertEqual(legacy, paths.resolve(paths.reference(legacy)))
        self.denied(lambda: paths.resolve(image, write=True))
        self.assertEqual(self.projects / 'new.json', paths.resolve('@projects/new.json', exists=False, write=True))
        automatic = self.paths(mounts={'arts': self.arts})
        self.assertEqual(self.projects / 'declared.json', automatic.resolve('@projects/declared.json', False, True))
        self.assertEqual('@projects/declared.json', automatic.reference(self.projects / 'declared.json'))
        self.assertEqual(self.marker.read_bytes(), b'parallel framework work fixture')

    def test_config_schema_fails_closed_for_versions_unknown_fields_and_path_shapes(self):
        for changes in [{'version': 2}, {'version': True}, {'version': 1.0}, {'unexpected': True}, {'legacy_root': ''},
                        {'state_root': []}, {'mounts': []}, {'mounts': {'Bad Name': str(self.arts)}},
                        {'project_write_roots': 'wrong'}, {'protected_roots': {}}, {'path_aliases': {}}]:
            with self.subTest(changes=changes):self.denied(lambda: WorkspacePaths.load(self.config(**changes)), 422)
        path = self.code / 'bad.json'
        for text in ['bad-json', '[]', '{}']:
            path.write_text(text);self.denied(lambda: WorkspacePaths.load(path), 422)

    def test_outside_traversal_unknown_mount_protected_and_missing_paths_are_denied(self):
        paths = self.paths();other = self.outside / 'secret.txt';other.write_text('not configured')
        for value in [other, '@missing/a', '@arts/../Projects/item.json', '@arts/../../Outside/secret.txt', self.marker,
                      self.protected / 'new.txt']:
            with self.subTest(value=str(value)):self.denied(lambda: paths.resolve(value, exists=False))
        self.denied(lambda: paths.resolve('@arts/missing.png'), 404)
        self.denied(lambda: paths.resolve(None), 422)
        self.denied(lambda: paths.resolve(self.code / 'new.py', exists=False, write=True))
        broad = self.paths(mounts={**self.mounts, 'workspace': self.base})
        self.denied(lambda: broad.resolve(self.marker))
        self.assertEqual(b'parallel framework work fixture', self.marker.read_bytes())

    def test_named_mount_cannot_be_rebased_by_absolute_tail_into_another_allowed_root(self):
        paths = self.paths();inside_other_mount = self.projects / 'project.json';inside_other_mount.write_text('{}')
        self.denied(lambda: paths.resolve('@arts/' + str(inside_other_mount)))

    def test_write_roots_cannot_overlap_protected_parent_or_child_and_explicit_root_is_writable(self):
        for allowed in [self.protected, self.protected / 'Child', self.protected.parent]:
            with self.subTest(allowed=allowed):self.denied(lambda: self.paths(project_write_roots=[allowed]), 422)
        shared = self.media / 'Rendered';shared.mkdir()
        paths = self.paths(project_write_roots=[shared])
        self.assertEqual(shared / 'result.mp4', paths.resolve(shared / 'result.mp4', False, True))
        self.denied(lambda: paths.resolve(self.media / 'other.mp4', False, True))
        policy = paths.export_policy();self.assertIn(str(shared), policy['write_roots'])
        self.assertIn(str(self.protected), policy['protected_roots']);self.assertIn(str(self.media), policy['read_roots'])
        policy['write_roots'].append('changed copy');self.assertNotIn('changed copy', paths.export_policy()['write_roots'])

    def test_longest_alias_chain_preserves_suffix_and_rejects_cycle_and_unknown_target(self):
        old = self.book / 'OldArts';specific = self.portraits / 'Special';specific.mkdir()
        generic = self.arts / 'generic.png';generic.write_bytes(b'generic')
        current = specific / 'Claire.png';current.write_bytes(b'identity')
        aliases = [{'from': str(old), 'to': '@arts'}, {'from': str(old / 'Characters'), 'to': str(specific)},
                   {'from': str(self.book / 'Earlier'), 'to': str(old / 'Characters')}]
        paths = self.paths(aliases=aliases)
        self.assertEqual(current, paths.resolve('Earlier/Claire.png'));self.assertEqual(generic, paths.resolve('OldArts/generic.png'))
        cycle = self.paths(aliases=[{'from': str(old), 'to': str(self.book / 'Loop')}, {'from': str(self.book / 'Loop'), 'to': str(old)}])
        self.denied(lambda: cycle.resolve(old / 'Claire.png', False), 422)
        growing = self.paths(aliases=[{'from': str(old), 'to': str(old / 'Nested')}])
        self.denied(lambda: growing.resolve(old / 'Claire.png', False), 422)
        self.denied(lambda: self.paths(aliases=[{'from': str(old), 'to': str(self.outside)}]))
        self.denied(lambda: self.paths(aliases=[{'from': str(old), 'to': str(old)}]), 422)

    def test_legacy_constructor_retains_relative_strings_and_old_book_write_boundary(self):
        paths = WorkspacePaths.legacy(self.book, self.amv, self.state)
        legacy = self.book / 'Arts/Claire.png';legacy.parent.mkdir();legacy.write_bytes(b'original')
        self.assertEqual('Arts/Claire.png', paths.reference(legacy));self.assertEqual(legacy, paths.resolve('Arts/Claire.png', write=True))
        self.assertEqual(self.state / 'new.json', paths.resolve(self.state / 'new.json', False, True))
        self.denied(lambda: paths.resolve(self.outside / 'not-book.json', False, True))

    def test_canonical_symlink_or_junction_cannot_escape_to_fake_protected_framework(self):
        link = self.book / 'linked-framework'
        try:link.symlink_to(self.protected, target_is_directory=True)
        except OSError:
            if os.name != 'nt':self.skipTest('Host does not permit temporary symlink creation')
            done = subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(self.protected)], capture_output=True, timeout=5)
            if done.returncode:self.skipTest('Host does not permit temporary NTFS junction creation')
        paths = self.paths()
        self.denied(lambda: paths.resolve(link / self.marker.name))
        self.denied(lambda: paths.resolve(link / 'written.txt', False, True))
        self.assertEqual(self.marker.read_bytes(), b'parallel framework work fixture')
        self.assertFalse((self.protected / 'written.txt').exists())
