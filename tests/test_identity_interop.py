import json
import os
import subprocess
import unittest

from test_supervisor import s


@unittest.skipUnless(os.environ.get('LAUNCHER_IDENTITY_PROBE'), 'Standalone C# probe is built in CI')
class IdentityInteropTests(unittest.TestCase):
    def test_editor_registration_identity_matches_native_birth_time(self):
        process = subprocess.Popen(['dotnet', os.environ['LAUNCHER_IDENTITY_PROBE']],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            identity = json.loads(process.stdout.readline())
            self.assertEqual(identity['pid'], process.pid)
            self.assertEqual(s.process_state(identity['pid'], identity['start']), 'alive')
            self.assertEqual(s.process_state(identity['pid'], str(int(identity['start']) + 10000000)), 'dead')
            process.stdin.close()
            self.assertEqual(process.wait(timeout=10), 0)
            self.assertEqual(s.process_state(identity['pid'], identity['start']), 'dead')
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()
