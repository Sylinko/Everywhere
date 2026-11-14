import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import pty
import select
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'installer.py'
if not SOURCE.exists():
    SOURCE = ROOT.parent / 'src/Everywhere.Linux/Update/linux-update.py'
spec = importlib.util.spec_from_file_location('installer', SOURCE)
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


def asset(data=b'fixture'):
    return {'url': 'https://downloads.example.test/payload', 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def catalog(channel='stable', version='1.2.0', data=b'fixture'):
    downloads = {'deb': asset(data), 'rpm': asset(data), 'tar.zst': asset(data)}
    release = {'version': version, 'assets': {'x64': downloads}}
    return {'schema': 1, 'channels': {channel: release}}


class InstallerTests(unittest.TestCase):
    def test_only_local_package_commands(self):
        for manager, prefix in [('apt-get', ['apt-get', '--no-remove', 'install']), ('dnf', ['dnf', 'install']), ('pacman', ['pacman', '-U'])]:
            self.assertEqual(u.package_command(manager, '/tmp/file'), prefix + ['/tmp/file'])

    def test_supported_systems_and_unknown_rejected(self):
        with patch.object(u.shutil, 'which', return_value='/bin/tool'), patch.object(u.Path, 'exists', return_value=False):
            for distro, version, manager in [('arch', '', 'pacman'), ('ubuntu', '24.04', 'apt-get'), ('debian', '14', 'apt-get'), ('fedora', '44', 'dnf')]:
                self.assertEqual(u.backend({'ID': distro, 'VERSION_ID': version}), manager)
            for distro in ['nixos', 'alpine', 'unknown']:
                with self.assertRaises(u.UpdateError):
                    u.backend({'ID': distro})

    def test_channel_missing_asset_and_wrong_channel_rejected(self):
        self.assertEqual(u.select_package(catalog(), 'stable', 'x64', 'pacman')[0], '1.2.0')
        for args in [('canary', 'x64', 'pacman'), ('stable', 'arm64', 'dnf')]:
            with self.assertRaises(u.UpdateError):
                u.select_package(catalog(), *args)
        with self.assertRaises(u.UpdateError):
            u.select_package(catalog('canary'), 'canary', 'x64', 'apt-get')

    def test_corrupted_and_insecure_downloads_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'payload'
            path.write_bytes(b'fixture')
            u.verify_file(path, asset())
            path.write_bytes(b'corrupt')
            with self.assertRaises(u.UpdateError):
                u.verify_file(path, asset())
            bad = dict(asset(), url='http://unsafe.test/file')
            with self.assertRaises(u.UpdateError):
                u.validate_asset(bad)

    def test_malformed_download_metadata_fails_with_a_clean_error(self):
        for value in (None, 123, []):
            with self.assertRaises(u.UpdateError):
                u.version_key(value)
        for value in (dict(asset(), url=None), dict(asset(), sha256=None)):
            with self.assertRaises(u.UpdateError):
                u.validate_asset(value)
        for value in ({'schema': 1, 'channels': []},
                      {'schema': 1, 'channels': {'stable': ['not a release']}},
                      {'schema': 1, 'channels': {'stable': {'version': '1.2.0', 'assets': []}}}):
            with self.assertRaises(u.UpdateError):
                u.select_package(value, 'stable', 'x64', 'apt-get')

    def test_arch_version_normalization(self):
        self.assertEqual(u.normalize_version('0.9.0canary.20261029.29-8', 'pacman'), '0.9.0-canary.20261029.29')
        self.assertEqual(u.normalize_version('1.2.0~canary.3', 'apt-get'), '1.2.0-canary.3')
        self.assertEqual(u.normalize_version('2:0.9.0canary.20261029.29-8', 'pacman'),
                         '0.9.0-canary.20261029.29')

    def test_installer_refresh_verified_cached_and_exit_code_propagated(self):
        data = b'print("new installer")'
        manifest = {'schema': 1, 'protocol': 1, 'version': '0.3.0', 'asset': asset(data)}
        with tempfile.TemporaryDirectory() as temporary, patch.object(u.Path, 'home', return_value=Path(temporary)), \
                patch.object(u, 'read_json', return_value=manifest), patch.object(u, 'fetch') as fetch, patch.object(u.subprocess, 'run') as run:
            fetch.side_effect = lambda url, path, limit: path.write_bytes(data)
            run.return_value.returncode = 17
            self.assertEqual(u.refresh_installer('https://site.test/installer.json', ['update']), 17)
            argv = run.call_args.args[0]
            self.assertIn('--no-installer-update', argv)
            self.assertEqual(Path(argv[1]).read_bytes(), data)
            self.assertEqual(u.refresh_installer('https://site.test/installer.json', ['update']), 17)
            self.assertEqual(fetch.call_count, 1)
            Path(argv[1]).write_bytes(b'corrupt cache')
            with self.assertRaises(u.UpdateError):
                u.refresh_installer('https://site.test/installer.json', ['update'])

    def test_provided_package_names_are_deduplicated(self):
        with patch.object(u, 'probe', return_value='everywhere-bin 1.2.0-1'):
            self.assertEqual(u.installed('pacman'), ('everywhere-bin', '1.2.0-1'))

    def test_fork_catalog_uses_release_assets_and_ignores_installer_tags(self):
        data = b'archive'
        url = 'https://github.com/Sylinko/Everywhere/releases/download/v1.2.0/Everywhere-Linux-x64-v1.2.0.tar.zst'
        releases = [{'tag_name': 'linux-installer-latest', 'prerelease': False},
                    {'tag_name': 'v1.2.0', 'prerelease': False, 'assets': [{
                        'name': 'Everywhere-Linux-x64-v1.2.0.tar.zst', 'state': 'uploaded',
                        'size': len(data), 'digest': 'sha256:' + hashlib.sha256(data).hexdigest(),
                        'browser_download_url': url}]}]
        with patch.object(u, 'fetch', return_value=json.dumps(releases).encode()) as fetch:
            target, item = u.select_package(u.read_catalog('github:Sylinko/Everywhere'), 'stable', 'x64', 'pacman')
            self.assertEqual(target, '1.2.0')
            self.assertEqual(item['url'], url)
            self.assertIn('api.github.com/repos/Sylinko/Everywhere/releases', fetch.call_args.args[0])

    def test_arch_archive_traversal_rejected_before_build(self):
        data = io.BytesIO()
        with tarfile.open(fileobj=data, mode='w') as archive:
            info = tarfile.TarInfo('../escape')
            info.size = 3
            archive.addfile(info, io.BytesIO(b'bad'))
        data.seek(0)
        with tempfile.TemporaryDirectory() as temporary, patch.object(u.os, 'geteuid', return_value=1000), \
                patch.object(u.shutil, 'which', return_value='/bin/tool'), patch.object(u.subprocess, 'Popen') as popen, \
                patch.object(u.subprocess, 'run') as run:
            popen.return_value.__enter__.return_value.stdout = data
            with self.assertRaises(u.UpdateError):
                u.prepare_arch(Path('/tmp/archive'), Path(temporary), '1.2.0', 'everywhere-bin', SOURCE, 0)
            run.assert_not_called()

    def test_arch_repackage_preserves_epoch_and_accepts_detached_signature(self):
        data = io.BytesIO()
        with tarfile.open(fileobj=data, mode='w') as archive:
            for name in ('Everywhere', 'Everywhere.Watchdog'):
                info = tarfile.TarInfo(name)
                info.size = 3
                archive.addfile(info, io.BytesIO(b'app'))
        data.seek(0)
        with tempfile.TemporaryDirectory() as temporary, patch.object(u.os, 'geteuid', return_value=1000), \
                patch.object(u.shutil, 'which', return_value='/bin/tool'), patch.object(u.subprocess, 'Popen') as popen, \
                patch.object(u, 'execute') as execute:
            directory = Path(temporary)
            package = directory / 'everywhere-bin-1.2.0-1-x86_64.pkg.tar.zst'
            def build(args, privileged, cwd):
                self.assertIn('--syncdeps', args)
                self.assertIn('epoch=2', (cwd / 'PKGBUILD').read_text())
                package.write_bytes(b'package')
                Path(str(package) + '.sig').write_bytes(b'signature')
            execute.side_effect = build
            popen.return_value.__enter__.return_value.stdout = data
            popen.return_value.__enter__.return_value.wait.return_value = 0
            self.assertEqual(u.prepare_arch(directory / 'archive', directory, '1.2.0',
                                            'everywhere-bin', SOURCE, 2), package)

    def test_bad_installer_never_executed(self):
        manifest = {'schema': 1, 'protocol': 1, 'version': '0.3.0', 'asset': asset()}
        with tempfile.TemporaryDirectory() as temporary, patch.object(u.Path, 'home', return_value=Path(temporary)), \
                patch.object(u, 'read_json', return_value=manifest), patch.object(u, 'fetch') as fetch, patch.object(u.subprocess, 'run') as run:
            fetch.side_effect = lambda url, path, limit: path.write_bytes(b'bad')
            with self.assertRaises(u.UpdateError):
                u.refresh_installer('https://site.test/installer.json', ['install'])
            run.assert_not_called()

    def test_metadata_transport_timeout_retries_then_uses_bundled_installer(self):
        with patch.dict(os.environ, {'EVERYWHERE_UPDATE_TRANSPORT': '["ew", "--linux-update-download"]'}), \
                patch.object(u.subprocess, 'run', side_effect=subprocess.TimeoutExpired('ew', 200)) as run, \
                patch.object(u.time, 'sleep'), patch.object(u.UI, 'status') as status:
            self.assertIsNone(u.refresh_installer('https://site.test/installer.json', ['update']))
            self.assertEqual(run.call_count, 3)
            status.assert_called_with('bundled_installer')

    def test_network_failure_uses_bundled_installer(self):
        for error in (OSError('offline'), u.DownloadError('application transport failed')):
            with patch.object(u, 'read_json', side_effect=error):
                self.assertIsNone(u.refresh_installer('https://site.test/installer.json', []))

    def test_new_installer_download_failure_uses_bundled_without_executing_partial_file(self):
        data = b'new installer'
        manifest = {'schema': 1, 'protocol': 1, 'version': '0.3.0', 'asset': asset(data)}
        for error in (u.DownloadError('offline'), u.DownloadError('HTTP 503')):
            with self.subTest(error=str(error)), tempfile.TemporaryDirectory() as temporary, \
                    patch.object(u.Path, 'home', return_value=Path(temporary)), \
                    patch.object(u, 'read_json', return_value=manifest), \
                    patch.object(u.subprocess, 'run') as run, patch.object(u.UI, 'status') as status:
                def interrupted_download(url, path, limit):
                    path.write_bytes(b'partial')
                    raise error
                with patch.object(u, 'fetch', side_effect=interrupted_download):
                    self.assertIsNone(u.refresh_installer('https://site.test/installer.json', ['update']))
                run.assert_not_called()
                status.assert_called_once_with('bundled_installer')
                self.assertEqual(list((Path(temporary) / '.cache/everywhere-installer').iterdir()), [])

    def run_flow(self, current=None, fail=False, corrupt=False, dry=False):
        data = b'package bytes'
        state = [current]
        calls = []
        def transaction(argv, privileged):
            calls.append(argv)
            if fail:
                raise subprocess.CalledProcessError(7, argv)
            state[0] = ('everywhere', '1.2.0')
        with patch.object(u.platform, 'system', return_value='Linux'), patch.object(u.platform, 'machine', return_value='x86_64'), \
                patch.object(u, 'distro_info', return_value={'ID': 'ubuntu'}), patch.object(u, 'backend', return_value='apt-get'), patch.object(u, 'installed', side_effect=lambda manager: state[0]), \
                patch.object(u, 'read_catalog', return_value=catalog(data=data)), patch.object(u, 'validate_package'), \
                patch.object(u, 'fetch', side_effect=lambda url, path, limit: path.write_bytes(b'bad' if corrupt else data)), \
                patch.object(u, 'execute', side_effect=transaction), tempfile.TemporaryDirectory() as temporary, \
                patch.object(u.Path, 'home', return_value=Path(temporary)):
            args = ['--no-installer-update', '--channel', 'stable'] + (['--dry-run'] if dry else [])
            result = u.main(args)
        return result, calls

    def test_first_install_and_upgrade(self):
        for current in [None, ('everywhere', '1.0.0')]:
            result, calls = self.run_flow(current=current)
            self.assertEqual(result, 0)
            self.assertEqual(calls[0][:3], ['apt-get', '--no-remove', 'install'])

    def test_preview_and_no_downgrade(self):
        for kwargs in [{'dry': True}, {'current': ('everywhere', '2.0.0')}]:
            result, calls = self.run_flow(**kwargs)
            self.assertEqual(result, 0)
            self.assertEqual(calls, [])

    def test_corruption_and_failed_transaction(self):
        with self.assertRaises(u.UpdateError):
            self.run_flow(corrupt=True)
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_flow(fail=True)

    def test_native_package_metadata_checked(self):
        with patch.object(u, 'probe', side_effect=['everywhere', '1.2.0', 'amd64']):
            u.validate_package(Path('/tmp/example.deb'), 'apt-get', '1.2.0', 'x64')
        with patch.object(u, 'probe', side_effect=['other-app', '1.2.0', 'amd64']):
            with self.assertRaises(u.UpdateError):
                u.validate_package(Path('/tmp/example.deb'), 'apt-get', '1.2.0', 'x64')

    @unittest.skipUnless(sys.platform.startswith('linux'), 'controlling-terminal acceptance runs on Linux')
    def test_menu_arrows_and_cancel_with_controlling_terminal(self):
        # forkpty supplies /dev/tty; the input stream is deliberately not a TTY.
        for keys, expected, code in [(b'\x1b[B\r', b'RESULT:canary', 0), (b'\r', b'RESULT:stable', 0), (b'\x1b', b'CANCELED', 0)]:
            pid, fd = pty.fork()
            if pid == 0:
                try:
                    os.environ['TERM'] = 'xterm'
                    os.environ['COLUMNS'] = '80'
                    sys.stdin = io.StringIO('script bytes, not keyboard input')
                    try:
                        print('RESULT:' + u.choose_channel(), flush=True)
                    except KeyboardInterrupt:
                        print('CANCELED', flush=True)
                    os._exit(0)
                except BaseException:
                    os._exit(1)
            output = b''
            deadline = time.monotonic() + 8
            sent = False
            try:
                while time.monotonic() < deadline:
                    if select.select([fd], [], [], 0.1)[0]:
                        try:
                            chunk = os.read(fd, 65536)
                        except OSError:
                            break
                        if not chunk:
                            break
                        output += chunk
                        if not sent and b'Navigate' in output:
                            os.write(fd, keys)
                            sent = True
                self.assertIn(expected, output)
                self.assertIn(b'\x1b[?25h', output)
            finally:
                os.close(fd)
                if not sent:
                    os.kill(pid, 9)
                _, status = os.waitpid(pid, 0)
                self.assertEqual(os.waitstatus_to_exitcode(status), code)


if __name__ == '__main__':
    unittest.main()
