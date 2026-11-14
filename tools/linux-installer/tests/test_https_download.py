"""Real loopback HTTPS coverage; no packages are installed and no app is launched."""
import hashlib
import http.server
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'installer.py'
if not SOURCE.exists():
    SOURCE = ROOT.parent / 'src/Everywhere.Linux/Update/linux-update.py'
spec = importlib.util.spec_from_file_location('https_installer', SOURCE)
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class TtyOutput(io.StringIO):
    def isatty(self):
        return True


@unittest.skipUnless(shutil.which('openssl'), 'openssl is required for the test certificate')
class HttpsDownloadTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix='everywhere-https-test-')
        cls.root = Path(cls.temporary.name)
        cls.payload = b'x' * (1024 * 1024)
        certificate = cls.root / 'cert.pem'
        key = cls.root / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                        '-keyout', str(key), '-out', str(certificate), '-days', '1',
                        '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost'],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == '/payload':
                    data = cls.payload
                elif self.path == '/installer.py':
                    data = SOURCE.read_bytes()
                elif self.path in ('/installer.json', '/bad.json'):
                    script = SOURCE.read_bytes()
                    digest = hashlib.sha256(script).hexdigest() if self.path != '/bad.json' else '0' * 64
                    data = json.dumps({'schema': 1, 'protocol': 1, 'version': u.INSTALLER_VERSION,
                                       'asset': {'url': cls.base + '/installer.py', 'size': len(script),
                                                 'sha256': digest}}).encode()
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                for offset in range(0, len(data), 65536):
                    self.wfile.write(data[offset:offset + 65536])
                    self.wfile.flush()
                    time.sleep(0.02)
            def log_message(self, *args):
                pass
        cls.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        cls.base = 'https://localhost:%d' % cls.server.server_port
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(certificate, key)
        cls.server.socket = context.wrap_socket(cls.server.socket, server_side=True)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=5)
        cls.temporary.cleanup()

    def verify_download(self, transport):
        observations, stream = [], TtyOutput()
        class ObservedProgress(u.DownloadProgress):
            def __init__(self, total):
                super().__init__(total, stream)
            def update(self, received, force=False):
                observations.append(received)
                super().update(received, force)
        with patch.dict(os.environ, {'SSL_CERT_FILE': str(self.root / 'cert.pem'),
                                     'EVERYWHERE_UPDATE_PROXY': 'none', 'TERM': 'xterm',
                                     'NO_PROXY': '*', 'no_proxy': '*',
                                     'EVERYWHERE_UPDATE_TRANSPORT': json.dumps(transport) if transport else ''}), \
                patch.object(u, 'DownloadProgress', ObservedProgress):
            destination = self.root / ('native' if transport else 'python')
            u.fetch(self.base + '/payload', destination, len(self.payload))
            self.assertEqual(destination.read_bytes(), self.payload)
        partial = set(value for value in observations if 0 < value < len(self.payload))
        self.assertGreaterEqual(len(partial), 2)
        self.assertIn('100%', stream.getvalue())
        self.assertIn('MiB', stream.getvalue())
        self.assertIn('/s', stream.getvalue())
        return observations

    def test_real_https_stream_updates_progress(self):
        self.verify_download(None)

    def test_existing_native_download_protocol_updates_progress(self):
        helper = os.environ.get('EVERYWHERE_NATIVE_DOWNLOAD_HELPER')
        if not helper:
            self.skipTest('Set EVERYWHERE_NATIVE_DOWNLOAD_HELPER to an existing ew executable')
        self.verify_download([helper, '--linux-update-download'])

    def bootstrap(self, manifest):
        if not sys.platform.startswith('linux'):
            self.skipTest('The bootstrap requires Linux')
        bootstrap = ROOT / 'install.sh'
        if not bootstrap.exists():
            self.skipTest('Bootstrap is maintained in the standalone installer directory')
        env = dict(os.environ, SSL_CERT_FILE=str(self.root / 'cert.pem'),
                   NO_PROXY='*', no_proxy='*', EVERYWHERE_INSTALLER_MANIFEST=self.base + manifest)
        return subprocess.run(['/bin/sh', str(bootstrap), '--installer-version'],
                              text=True, capture_output=True, timeout=30, env=env)

    def test_bootstrap_downloads_verifies_and_runs_the_real_engine(self):
        result = self.bootstrap('/installer.json')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(u.INSTALLER_VERSION, result.stdout)
        self.assertIn('Verifying', result.stdout)
        self.assertIn('Download', result.stderr)
        self.assertNotIn('\x1b', result.stdout + result.stderr)

    def test_bootstrap_rejects_corruption_without_a_traceback(self):
        result = self.bootstrap('/bad.json')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Installer checksum mismatch.', result.stderr)
        self.assertNotIn('Traceback', result.stderr)
        self.assertNotIn(u.INSTALLER_VERSION, result.stdout)


if __name__ == '__main__':
    unittest.main()
