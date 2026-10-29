import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('updater', Path(__file__).resolve().parents[2] / 'src/Everywhere.Linux/Update/linux-update.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class ApplicationTransportTests(unittest.TestCase):
    def test_application_transport_returns_metadata_and_downloads_files(self):
        with tempfile.TemporaryDirectory() as directory:
            helper = Path(directory) / 'helper.py'
            helper.write_text('import sys\nfrom pathlib import Path\nPath(sys.argv[2]).write_bytes(b"fixture")\n')
            env = {'EVERYWHERE_UPDATE_TRANSPORT': json.dumps([sys.executable, str(helper)])}
            with patch.dict(os.environ, env):
                self.assertEqual(u.fetch('https://example.invalid/metadata'), b'fixture')
                output = Path(directory) / 'download'
                self.assertEqual(u.fetch('https://example.invalid/asset', output, 7), b'')
                self.assertEqual(output.read_bytes(), b'fixture')
                with self.assertRaises(u.UpdateError):
                    u.fetch('https://example.invalid/asset', output, 6)

    def test_transport_failure_never_uses_another_network_source(self):
        with patch.dict(os.environ, {'EVERYWHERE_UPDATE_TRANSPORT': json.dumps(['fake-helper'])}), \
             patch.object(u.subprocess, 'run') as run, patch.object(u.time, 'sleep'), \
             patch.object(u.urllib.request, 'build_opener') as opener:
            run.return_value.returncode = 1
            with self.assertRaises(u.UpdateError):
                u.fetch('https://example.invalid/asset')
            self.assertEqual(run.call_count, 3)
            opener.assert_not_called()

    def test_invalid_transport_is_rejected(self):
        with patch.dict(os.environ, {'EVERYWHERE_UPDATE_TRANSPORT': '"not an argv array"'}):
            with self.assertRaises(u.UpdateError):
                u.fetch('https://example.invalid/asset')

    def test_insecure_url_does_not_start_transport(self):
        with patch.dict(os.environ, {'EVERYWHERE_UPDATE_TRANSPORT': '["fake-helper"]'}), patch.object(u.subprocess, 'run') as run:
            with self.assertRaises(u.UpdateError):
                u.fetch('http://example.invalid/asset')
            run.assert_not_called()
