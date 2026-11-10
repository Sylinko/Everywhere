"""Run the real CLI/probes/transaction boundary with fake executables in an empty PATH."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'installer.py'
if not SCRIPT.exists():
    SCRIPT = ROOT.parent / 'src/Everywhere.Linux/Update/linux-update.py'
PAYLOAD = b'isolated fixture'


class ProcessFlowTests(unittest.TestCase):
    def test_help_uses_public_command_and_does_not_claim_preview_default(self):
        result = subprocess.run([sys.executable, str(SCRIPT), '--help'], text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('usage: Everywhere installer', result.stdout)
        self.assertNotIn('default: preview only', result.stdout)

    def run_cli(self, apply=False, fail=False, corrupt=False):
        with tempfile.TemporaryDirectory(prefix='everywhere-updater-test-') as temporary:
            root = Path(temporary)
            bindir = root / 'bin'
            bindir.mkdir()
            state = root / 'installed'
            state.write_text('1.0.0')
            log = root / 'transaction.log'
            scripts = {
                'dpkg-query': '#!/bin/sh\nversion=$(/bin/cat "$TEST_STATE")\nprintf "install ok installed\\t%s" "$version"\n',
                'dpkg-deb': '#!/bin/sh\ncase "$3" in Package) printf everywhere;; Version) printf 1.2.0;; Architecture) printf amd64;; esac\n',
                'sudo': '#!/bin/sh\n[ "$1" = -- ] && shift\nexec "$@"\n',
                'apt-get': '#!/bin/sh\nprintf "%s\\n" "$@" > "$TEST_LOG"\n[ "$TEST_FAIL" = 1 ] && exit 7\nprintf 1.2.0 > "$TEST_STATE"\n',
            }
            for name, body in scripts.items():
                path = bindir / name
                path.write_text(body)
                path.chmod(0o755)
            name = 'Everywhere-Linux-x64-v1.2.0.deb'
            fixture = root / 'release.json'
            fixture.write_text(json.dumps({'schema': 1, 'channels': {'stable': {'version': '1.2.0', 'assets': {'x64': {'deb': {'sha256': hashlib.sha256(PAYLOAD).hexdigest(), 'size': len(PAYLOAD), 'url': 'https://website.test/' + name}}}}}}))
            # Only host identity and the remote byte transport are substituted.
            # Real probe subprocesses, hashing, package metadata checks, locking,
            # privilege routing, transaction exit codes and postcheck are exercised.
            runner = '''import importlib.util, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location('updater', sys.argv[1])
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)
u.platform.system = lambda: 'Linux'
u.platform.machine = lambda: 'x86_64'
u.distro_info = lambda: {'ID': 'ubuntu', 'VERSION_ID': '24.04'}
def fetch(url, destination, limit):
    Path(destination).write_bytes(%r)
u.fetch = fetch
try:
    sys.exit(u.main(sys.argv[2:]))
except Exception as exc:
    print(str(exc), file=sys.stderr)
    sys.exit(1)
''' % (b'bad' if corrupt else PAYLOAD)
            env = dict(os.environ, PATH=str(bindir), HOME=str(root), TEST_STATE=str(state), TEST_LOG=str(log), TEST_FAIL='1' if fail else '0')
            argv = [sys.executable, '-c', runner, str(SCRIPT), '--catalog', str(fixture), '--no-installer-update', '--channel', 'stable'] + ([] if apply else ['--dry-run'])
            result = subprocess.run(argv, env=env, text=True, capture_output=True, timeout=20)
            return result, state.read_text(), log.read_text() if log.exists() else ''

    def test_preview_never_invokes_transaction(self):
        result, version, log = self.run_cli()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(version, '1.0.0')
        self.assertEqual(log, '')

    def test_apply_verifies_installed_target(self):
        result, version, log = self.run_cli(apply=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(version, '1.2.0')
        self.assertIn('--no-remove\ninstall\n', log)
        self.assertIn('Everywhere installed successfully', result.stdout)

    def test_failed_transaction_preserves_error(self):
        result, version, log = self.run_cli(apply=True, fail=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(version, '1.0.0')
        self.assertTrue(log)
        self.assertNotIn('Everywhere installed successfully', result.stdout)

    def test_corrupt_download_never_invokes_transaction(self):
        result, version, log = self.run_cli(apply=True, corrupt=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(version, '1.0.0')
        self.assertEqual(log, '')


if __name__ == '__main__':
    unittest.main()
