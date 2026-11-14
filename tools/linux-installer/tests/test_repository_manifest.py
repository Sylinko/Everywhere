"""Keep repository-hosted installer metadata consistent with the executable bytes."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class RepositoryManifestTests(unittest.TestCase):
    def test_committed_manifest_matches_installer(self):
        result = subprocess.run([sys.executable, str(ROOT / 'build-manifest.py'), '--check'],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_modified_script_requires_regenerating_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for name in ('build-manifest.py', 'installer.py', 'installer.json'):
                shutil.copyfile(ROOT / name, directory / name)
            script = directory / 'installer.py'
            script.write_bytes(script.read_bytes() + b'\n# Updated installer payload.\n')
            command = [sys.executable, str(directory / 'build-manifest.py')]
            stale = subprocess.run([*command, '--check'], capture_output=True, text=True, timeout=10)
            self.assertNotEqual(stale.returncode, 0)
            self.assertIn('manifest is stale', stale.stderr)
            subprocess.run(command, check=True, capture_output=True, timeout=10)
            subprocess.run([*command, '--check'], check=True, capture_output=True, timeout=10)


if __name__ == '__main__':
    unittest.main()
