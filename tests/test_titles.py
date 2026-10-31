import copy
import math
import unittest
from pathlib import Path
from unittest.mock import patch

from scenario_fixtures import ScenarioFixture
from studio import export_plan, titles, timeline
from studio.export_worker import DraftRenderer
from studio.model import StudioError, atomic, digest


def title(**patch):
    return titles.new_row({'text': 'Сказки\nИстинной Луны', 'font_size': 20, 'outline': False,
                           'duration_frames': 8, 'fade_in_frames': 2, 'fade_out_frames': 2, **patch}, identifier='title-one')


class TitleMathTests(unittest.TestCase):
    def test_catalog_is_public_and_resolve_font_restricts_ids_with_normalized_cyrillic_defaults(self):
        fonts = titles.catalog();self.assertGreater(len(fonts), 0)
        for font in fonts:
            self.assertEqual({'id', 'label'}, set(font));self.assertTrue(titles.resolve_font(font['id']).is_file())
        for invalid in ['../arial', 'C:/Windows/Fonts/arial.ttf', '', None, []]:
            with self.subTest(font=invalid), self.assertRaises(timeline.TimelineError):titles.resolve_font(invalid)
        defaults = titles.defaults(8)
        self.assertEqual((0, 24, 2, 2, 'center', 'center', 'transparent', True), tuple(defaults[k] for k in
                         ['start_frame', 'duration_frames', 'fade_in_frames', 'fade_out_frames', 'align', 'position', 'background', 'outline']))
        row = titles.new_row({'text': 'Клэр\r\nПринято\rСумеречный\tдвор'}, identifier='stable')
        self.assertEqual('Клэр\nПринято\nСумеречный    двор', row['text'])
        changed = titles.new_row({'id': 'replacement', 'name': 'New'}, old=row)
        self.assertEqual('stable', changed['id']);self.assertEqual('New', changed['name']);self.assertEqual('Титр', row['name'])

    def test_opacity_midpoint_fades_half_open_and_exact_sum_duration(self):
        row = title(start_frame=4)
        self.assertEqual([0, .25, .75, 1, 1, .75, .25, 0], [titles.opacity(row, f) for f in [3, 4, 5, 6, 9, 10, 11, 12]])
        touching = title(start_frame=0, duration_frames=4, fade_in_frames=2, fade_out_frames=2)
        self.assertEqual([.25, .75, .75, .25], [titles.opacity(touching, f) for f in range(4)])
        hard = title(start_frame=0, duration_frames=1, fade_in_frames=0, fade_out_frames=0)
        self.assertEqual([0, 1, 0], [titles.opacity(hard, f) for f in [-1, 0, 1]])

    def test_validation_64_rows_four_overlapping_half_open_and_invalid_shapes_are_exact(self):
        valid = title()
        titles.validate({}, {'titles': [{**valid, 'id': f't{i}', 'start_frame': i * 8} for i in range(64)]})
        four = [{**valid, 'id': f't{i}'} for i in range(4)]
        titles.validate({}, {'titles': four + [{**valid, 'id': 'touch', 'start_frame': 8}]})
        invalid = [None, {}, [None], [{}], [valid, valid], [{**valid, 'id': f't{i}', 'start_frame': i * 8} for i in range(65)],
                   four + [{**valid, 'id': 'fifth', 'start_frame': 7}]]
        changes = [{'text': ''}, {'text': 'я' * 1001}, {'text': 'я\n' * 12 + 'я'}, {'text': 'bad\x01'},
                   {'start_frame': -1}, {'duration_frames': 0}, {'duration_frames': True}, {'fade_in_frames': 7},
                   {'fade_out_frames': math.nan}, {'font_id': '../arial'}, {'font_size': 7}, {'font_size': 257},
                   {'font_size': True}, {'color': '#FFF'}, {'color': 'red'}, {'align': []}, {'position': 'top'},
                   {'background': 'white'}, {'outline': 1}, {'start_frame': timeline.MAX_FRAME}]
        invalid += [[{**valid, **change}] for change in changes]
        for rows in invalid:
            with self.subTest(rows=str(rows)[:100]), self.assertRaises(timeline.TimelineError):titles.validate({}, {'titles': rows})
        for change in [{'font_size': 8}, {'font_size': 256}, {'text': 'я' * 1000}, {'text': 'я\n' * 11 + 'я'}, {'color': '#aBcDEF'}]:
            titles.validate({}, {'titles': [{**valid, **change}]})


class TitleRasterTests(ScenarioFixture):
    def setUp(self):
        super().setUp();self.p = self.current();self.p['output_size'] = [320, 180]

    def test_cyrillic_multiline_wrap_rgba_canonical_alignment_font_hash_and_cache_exclusions(self):
        from PIL import Image
        row = title(text='Клэр приходит в Сумеречный двор\nПринято', align='left', position='lower')
        before, sources = self.project_state(), self.source_state()
        layer = titles.render_layer(self.store, self.p, row)
        self.assertEqual([320, 180], layer['canvas_size']);self.assertEqual(26, layer['x'])
        self.assertEqual(round(180 * .92 - layer['height']), layer['y'])
        self.assertGreater(layer['wrapped_text'].count('\n'), row['text'].count('\n'))
        with Image.open(layer['path']) as image:
            self.assertEqual('RGBA', image.mode);self.assertEqual((layer['width'], layer['height']), image.size)
            self.assertIsNotNone(image.getbbox());self.assertLess(image.width, 320);self.assertLess(image.height, 180)
        self.assertEqual(digest(layer['path']), layer['sha256']);self.assertEqual(digest(titles.resolve_font(row['font_id'])), layer['font_sha256'])
        same = titles.render_layer(self.store, self.p, {**row, 'id': 'different', 'name': 'Other', 'start_frame': 50,
                                   'duration_frames': 20, 'fade_in_frames': 3, 'fade_out_frames': 4, 'background': 'black'})
        self.assertEqual((layer['key'], layer['path'], layer['sha256']), tuple(same[k] for k in ['key', 'path', 'sha256']))
        self.assertEqual('different', same['id']);self.assertEqual('black', same['background'])
        self.assertEqual(before, self.project_state());self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_raster_key_tracks_each_style_font_fingerprint_and_output_size_and_rejects_overflow(self):
        row = title(text='Клэр')
        base = titles.render_layer(self.store, self.p, row)
        for change in [{'text': 'Наставница'}, {'font_size': 21}, {'color': '#FFCC00'}, {'align': 'right'}, {'position': 'upper'}, {'outline': True}]:
            with self.subTest(change=change):self.assertNotEqual(base['key'], titles.render_layer(self.store, self.p, {**row, **change})['key'])
        large = titles.render_layer(self.store, {**self.p, 'output_size': [640, 360]}, row)
        self.assertNotEqual(base['key'], large['key']);self.assertEqual([640, 360], large['canvas_size'])
        font_path = titles.resolve_font(row['font_id'])
        def fingerprint(path):return 'f' * 64 if Path(path) == font_path else digest(path)
        with patch('studio.titles.digest', side_effect=fingerprint):changed = titles.render_layer(self.store, self.p, row)
        self.assertNotEqual(base['key'], changed['key']);self.assertEqual('f' * 64, changed['font_sha256'])
        with self.assertRaises(timeline.TimelineError):titles.render_layer(self.store, {**self.p, 'output_size': [64, 48]}, title(text='Ш', font_size=256))

    def test_compositor_group_black_opacity_transparent_and_layer_order_are_numeric_and_source_immutable(self):
        import numpy as np
        from PIL import Image
        path = self.book / 'synthetic-title.png';rgba = np.full((2, 2, 4), 255, np.uint8);rgba[1, 1, 3] = 128
        Image.fromarray(rgba).save(path)
        layer = {'id': 'title-one', 'asset_id': 'png', 'canvas_size': [4, 4], 'x': 1, 'y': 1, 'width': 2, 'height': 2}
        row = title(start_frame=0, duration_frames=4, fade_in_frames=2, fade_out_frames=0, background='black')
        request = {'book': str(self.book), 'size': [4, 4], 'assets': {'png': {'path': str(path)}}, 'titles': [row], 'title_layers': [layer]}
        pixels = np.full((4, 4, 3), 100, np.uint8);original = pixels.copy();result = titles.TitleCompositor(request).compose(pixels, 0)
        self.assertEqual([139, 139, 139], result[1, 1].tolist());self.assertEqual([107, 107, 107], result[2, 2].tolist())
        self.assertEqual([75, 75, 75], result[0, 0].tolist());self.assertTrue(np.array_equal(original, pixels))
        transparent = copy.deepcopy(request);transparent['titles'][0]['background'] = 'transparent'
        result = titles.TitleCompositor(transparent).compose(pixels, 0)
        self.assertEqual([139] * 3, result[1, 1].tolist());self.assertEqual([100] * 3, result[0, 0].tolist())
        # List order is visual order even though the layer lookup is keyed by stable IDs.
        blue = self.book / 'blue.png';Image.new('RGBA', (2, 2), (0, 0, 255, 255)).save(blue)
        mixed = copy.deepcopy(request);mixed['titles'] = [{**row, 'background': 'transparent', 'fade_in_frames': 0}, {**row, 'id': 'top', 'background': 'transparent', 'fade_in_frames': 0}]
        mixed['assets']['blue'] = {'path': str(blue)};mixed['title_layers'].append({**layer, 'id': 'top', 'asset_id': 'blue'})
        self.assertEqual([0, 0, 255], titles.TitleCompositor(mixed).compose(pixels, 1)[1, 1].tolist())
        mixed['titles'].reverse();self.assertEqual([255] * 3, titles.TitleCompositor(mixed).compose(pixels, 1)[1, 1].tolist())

    def test_compositor_lru_is_bounded_four_and_layer_paths_stay_inside_book(self):
        import numpy as np
        from PIL import Image
        path = self.book / 'glyph.png';Image.new('RGBA', (1, 1), 'white').save(path)
        rows = [{**title(start_frame=i, duration_frames=1, fade_in_frames=0, fade_out_frames=0), 'id': f't{i}'} for i in range(5)]
        layers = [{'id': r['id'], 'asset_id': 'png', 'canvas_size': [4, 4], 'x': 1, 'y': 1, 'width': 1, 'height': 1} for r in rows]
        request = {'book': str(self.book), 'size': [4, 4], 'assets': {'png': {'path': str(path)}}, 'titles': rows, 'title_layers': layers}
        compositor = titles.TitleCompositor(request);pixels = np.zeros((4, 4, 3), np.uint8)
        for frame in range(5):compositor.compose(pixels, frame)
        self.assertEqual(['t1', 't2', 't3', 't4'], list(compositor.cache))
        compositor.compose(pixels, 0);self.assertEqual(['t2', 't3', 't4', 't0'], list(compositor.cache))
        outside = self.base / 'outside.png';Image.new('RGBA', (1, 1), 'white').save(outside)
        request['assets']['png']['path'] = str(outside)
        with self.assertRaises(ValueError):titles.TitleCompositor(request).compose(pixels, 0)


class TitleStoreTests(ScenarioFixture):
    def setUp(self):
        super().setUp();p = self.current();p['output_size'] = [320, 180]
        self.store.commit(p, p['revision'], 'fixture', 'Canonical title canvas')

    def op(self, action, **payload):
        return self.store.timeline_operation(self.pid, self.current()['revision'], action, payload)

    def payload(self, **changes):
        row = title(**changes);return {k: row[k] for k in titles.FIELDS}

    def refused(self, action, payload, revision=None, status=422):
        before, sources = self.project_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'] if revision is None else revision, action, payload)
        self.assertEqual(status, caught.exception.status);self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_legacy_reads_add_update_remove_and_undo_keep_other_montage_source_and_later_notes(self):
        before, sources = self.current(), self.source_state();state = self.project_state()
        self.assertEqual([], self.store.title_layers(self.pid)['layers'])
        self.assertNotIn('titles', self.current()['timeline']);self.assertEqual(state, self.project_state())
        p = self.op('add_title', **self.payload());row = p['timeline']['titles'][0]
        self.assertTrue(row['id'].startswith('title-'))
        for key in ['segments', 'clips', 'music', 'markers', 'clip_audio']:self.assertEqual(before['timeline'][key], p['timeline'][key])
        self.assertEqual(before['blocks'], p['blocks']);self.assertEqual(before['assets'], p['assets'])
        p = self.op('update_title', id=row['id'], name='Автор', text='Сказки истинной луны', start_frame=24)
        self.assertEqual((row['id'], 'Автор', 24), tuple(p['timeline']['titles'][0][k] for k in ['id', 'name', 'start_frame']))
        updated = copy.deepcopy(p['timeline']['titles'][0]);self.save({'notes': 'Later source note'})
        notes = self.current()['blocks'];self.op('remove_title', id=row['id']);self.assertEqual([], self.current()['timeline']['titles'])
        self.op('undo');self.assertEqual([updated], self.current()['timeline']['titles']);self.assertEqual(notes, self.current()['blocks'])
        self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_invalid_layout_and_revision_refuse_before_commit_and_preview_is_read_only(self):
        p = self.op('add_title', **self.payload());row = p['timeline']['titles'][0]
        for changes in [{'font_size': 256, 'text': 'Ш'}, {'text': ''}, {'font_id': '../arial'}, {'fade_in_frames': 7}, {'outline': 1}]:
            self.refused('update_title', {'id': row['id'], **changes})
        self.refused('add_title', self.payload(), self.current()['revision'] - 1, 409)
        self.refused('remove_title', {'id': row['id']}, True, 409)
        before = self.project_state();preview = self.store.title_preview(self.pid, self.current()['revision'], self.payload(name='Preview', start_frame=100))
        self.assertEqual(self.current()['revision'], preview['project_revision']);self.assertNotIn('path', preview['layer'])
        self.assertTrue(self.store.title_image(self.pid, preview['layer']['key']).is_file());self.assertEqual(before, self.project_state())
        with self.assertRaises(StudioError) as caught:self.store.title_preview(self.pid, self.current()['revision'] - 1, self.payload())
        self.assertEqual(409, caught.exception.status)
        for key in ['../arial', 'a' * 63, 'x' * 64]:
            with self.assertRaises(StudioError):self.store.title_image(self.pid, key)
        with patch.object(self.store, 'render_title', return_value={'width': 4000, 'height': 4000}):
            self.assertEqual([{'width': 4000, 'height': 4000}], self.store.render_title_layers(self.current()))
        with patch.object(self.store, 'render_title', side_effect=[{'width': 4000, 'height': 4000}, {'width': 1, 'height': 1}]):
            self.refused('add_title', self.payload(name='One pixel over aggregate budget'))

    def test_title_only_extent_and_frozen_layers_hash_detect_changed_raster_without_project_write(self):
        import av
        import numpy as np
        p = self.current();p['timeline'].update(segments=[], clips=[], music=None)
        self.store.commit(p, p['revision'], 'fixture', 'Title-only composition')
        self.op('add_title', **self.payload(start_frame=24, duration_frames=24))
        before = self.project_state();request = export_plan.plan(self.store, self.pid, self.current()['revision'], 'project')
        self.assertEqual((48, 2, []), tuple(request[k] for k in ['frames', 'seconds', 'clips']))
        layer = request['title_layers'][0];asset = request['assets'][layer['asset_id']]
        self.assertEqual(layer['sha256'], asset['expected_sha256']);self.assertEqual(digest(asset['path']), layer['sha256'])
        self.assertEqual([320, 180], layer['canvas_size'])
        folder = self.book / 'TitleRequest';folder.mkdir();atomic(folder / 'Request.json', request)
        request['timeout_seconds'] = 15;atomic(folder / 'Request.json', request)
        result = DraftRenderer(folder).run()
        self.assertEqual((48, 24, 2), tuple(result['video_info'][k] for k in ['frames', 'fps', 'seconds']))
        with av.open(str(folder / 'Draft.mp4')) as output:frames = [f.to_ndarray(format='rgb24') for f in output.decode(video=0)]
        self.assertEqual(0, int(frames[23].max()));self.assertGreater(int(frames[24].max()), 30)
        self.assertLess(int(frames[24].max()), 100);self.assertGreater(int(frames[26].max()), 220)
        request['titles'][0]['text'] = 'Changed request'
        self.assertEqual(before, self.project_state())
        Path(asset['path']).write_bytes(Path(asset['path']).read_bytes() + b'changed derived PNG')
        with self.assertRaisesRegex(ValueError, 'Слой титра изменился'):DraftRenderer(folder).inputs()

    def test_real_cpu_title_group_fades_draw_after_black_transition_without_audio_change(self):
        import av
        import fractions
        import numpy as np
        from PIL import Image
        from audio_fixtures import write_wav
        from studio.transitions import windows
        p = self.current();p['output_size'] = [160, 90]
        row = title(text='Клэр', font_size=16, start_frame=0, duration_frames=18,
                    fade_in_frames=2, fade_out_frames=2, background='black')
        layer = titles.render_layer(self.store, p, row)
        with Image.open(layer['path']) as glyph:rgba = np.asarray(glyph)
        yy, xx = np.argwhere(rgba[:, :, 3] == 255)[0];y, x = layer['y'] + int(yy), layer['x'] + int(xx)
        source = self.book / 'gray.mkv'
        with av.open(str(source), 'w') as out:
            stream = out.add_stream('ffv1', rate=8);stream.width = 160;stream.height = 90;stream.pix_fmt = 'bgr0'
            for index in range(8):
                frame = av.VideoFrame.from_ndarray(np.full((90, 160, 3), 200, np.uint8), format='rgb24');frame.pts = index;frame.time_base = fractions.Fraction(1, 8)
                for packet in stream.encode(frame):out.mux(packet)
            for packet in stream.encode(None):out.mux(packet)
        music = self.book / 'continuous.wav';rate = 48000
        signal = (.2 * np.sin(2 * np.pi * 1000 * np.arange(108000) / rate) * 32767).astype(np.int16)
        write_wav(music, [(int(v), int(v)) for v in signal], rate=rate)
        clips = [{'id': cid, 'asset_id': 'v', 'start_frame': start, 'duration_frames': 8,
                  'source_in_frame': 0, 'source_out_frame': 8, 'speed': 1} for cid, start in [('a', 0), ('b', 10)]]
        transition = {'left_clip_id': 'a', 'right_clip_id': 'b', 'fade_out_frames': 2, 'pause_frames': 4, 'fade_in_frames': 2}
        request = {'book': str(self.book), 'size': [160, 90], 'fps': 8, 'frames': 18, 'timeout_seconds': 15, 'clips': clips, 'clip_audio': False,
                   'assets': {'v': {'path': str(source)}, 'm': {'path': str(music)}, 'png': {'path': layer['path'], 'expected_sha256': layer['sha256']}},
                   'titles': [row], 'title_layers': [{**layer, 'asset_id': 'png'}], 'transition_windows': windows({'clips': clips, 'transitions': [transition]}),
                   'music': {'asset_id': 'm', 'start_frame': 0, 'source_in_frame': 0, 'duration_frames': 18, 'gain': 1, 'muted': False}}
        folder = self.book / 'TitleExport';folder.mkdir();atomic(folder / 'Request.json', request)
        hashes = {path: digest(path) for path in [source, music, Path(layer['path'])]};result = DraftRenderer(folder).run()
        self.assertEqual((18, 8, 2.25), tuple(result['video_info'][k] for k in ['frames', 'fps', 'seconds']))
        with av.open(str(folder / 'Draft.mp4')) as out:frames = [f.to_ndarray(format='rgb24') for f in out.decode(video=0)]
        for frame, glyph_level, background in [(0, 214, 150), (1, 241, 50), (7, 255, 0), (16, 241, 50), (17, 214, 150)]:
            self.assertAlmostEqual(glyph_level, float(frames[frame][y, x].mean()), delta=18, msg=f'Glyph frame{frame}')
            self.assertAlmostEqual(background, float(frames[frame][5, 5].mean()), delta=12, msg=f'Background frame{frame}')
        with av.open(str(folder / 'Draft.mp4')) as out:audio = np.concatenate([f.to_ndarray() for f in out.decode(audio=0)], axis=1)[0]
        rms = lambda lo, hi: float(np.sqrt(np.mean(audio[round(lo * rate):round(hi * rate)] ** 2)))
        self.assertAlmostEqual(.2 / math.sqrt(2), rms(.1, .4), delta=.008)
        self.assertAlmostEqual(rms(.1, .4), rms(.85, 1.15), delta=.004)
        self.assertEqual(hashes, {path: digest(path) for path in hashes});self.assert_no_generation()
