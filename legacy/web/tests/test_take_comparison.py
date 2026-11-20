from fixtures import Fixture
from studio.model import atomic, digest
from studio.take_comparison import describe


class TakeComparisonTests(Fixture):
    def test_linked_job_and_archive_describe_inputs_without_using_current_block(self):
        take, _ = self.add_take()
        p = self.current()
        recorded = self.block(p)['takes'][0]
        job_id = 'job-0123456789abcdef'
        recorded.update(job_id=job_id, video_info={'fps': 24, 'seconds': 1, 'frames': 24})
        folder = self.jobs.directory(self.pid, job_id)
        (folder / 'Input').mkdir(parents=True)
        (folder / 'Run').mkdir()
        archive = self.amv / 'Archived generation'
        archive.mkdir()
        self.block(p)['takes'].append({**recorded, 'id': 'archive', 'job_id': None,
                                       'job_path': self.store.relative(archive)})
        self.block(p)['takes'].append({**recorded, 'id': 'legacy', 'job_id': None})
        self.store.commit(p, p['revision'], 'test', 'Take metadata fixture')
        config = {'seed': 18446744073709551613, 'seconds': 2, 'profile': 'omni-guided',
                  'references': [{'path': str(self.images[0]), 'role': 'character'}],
                  'guides': [{'picture': 1, 'frame': 0}]}
        atomic(folder / 'Input' / 'Config.json', config)
        atomic(folder / 'Run' / 'Build.json', {'models': 'Model A', 'source_input_hashes': [
            {'Source': str(self.images[0]), 'SHA256': digest(self.images[0])}
        ]})
        atomic(archive / 'Job.json', {**config, 'profile': 'omni-context', 'seed': 18446744073709551614})
        project_hash = digest(self.store.location(self.pid))
        a = describe(self.store, self.jobs, self.pid, take['id'])
        b = describe(self.store, self.jobs, self.pid, 'archive')
        legacy = describe(self.store, self.jobs, self.pid, 'legacy')

        self.assertEqual(('18446744073709551613', 'omni-guided', 'Model A'),
                         (a['seed'], a['profile'], a['models']))
        self.assertEqual(('18446744073709551614', 'omni-context'), (b['seed'], b['profile']))
        self.assertEqual(('ref-1.png', [0], digest(self.images[0])),
                         tuple(a['references'][0][key] for key in ('file', 'guides', 'sha256')))
        self.assertEqual(a['references'][0]['source_key'], b['references'][0]['source_key'])
        self.assertIsNone(legacy['seed'])
        self.assertIsNone(legacy['profile'])
        self.assertIsNone(legacy['references'])
        self.assertEqual({'fps': 24, 'seconds': 1, 'frames': 24}, legacy['video'])
        self.assertEqual(project_hash, digest(self.store.location(self.pid)))
