"""Short CPU PCM fixtures; no installed media or GPU services are used."""
import math
import struct
import wave
from fixtures import Fixture


def write_wav(path, rows, rate=16000):
    rows = list(rows)
    channels = len(rows[0]) if rows else 1
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), 'wb') as stream:
        stream.setnchannels(channels)
        stream.setsampwidth(2)
        stream.setframerate(rate)
        stream.writeframes(b''.join(struct.pack('<' + 'h' * channels, *row) for row in rows))


def wave_data(seconds=2.0, peak=.25, channels=1):
    return {'version': 1, 'seconds': seconds, 'sample_rate': 16000, 'source_sample_rate': 16000,
            'channels': channels, 'peaks_per_second': 100,
            'peaks': [peak] * math.ceil(seconds * 100 - 1e-8), 'analysis_seconds': .001}


class AudioFixture(Fixture):
    def setUp(self):
        super().setUp()
        self.audio = self.book / 'Sounds/music track.wav'
        write_wav(self.audio, [(8192,)] * 32000)
        p = self.current()
        self.aid = self.store.asset(p, self.audio)
        self.store.commit(p, p['revision'], 'fixture', 'Audio source')

    def op(self, action, **payload):
        return self.store.timeline_operation(self.pid, self.current()['revision'], action, payload)

    def state_bytes(self):
        from studio.model import digest
        location = self.store.location(self.pid)
        return (location.read_bytes(), {f.name: digest(f) for f in (location.parent / 'History').glob('*.json')})

    def music_payload(self, **patch):
        return {'asset_id': self.aid, 'start_frame': 0, 'source_in_frame': 0,
                'duration_frames': 48, 'gain': .8, 'muted': False, **patch}
