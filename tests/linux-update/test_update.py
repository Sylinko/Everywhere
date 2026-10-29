import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('updater', Path(__file__).resolve().parents[2] / 'src/Everywhere.Linux/Update/linux-update.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)
PAYLOAD = b'isolated package fixture; never a real installation'


def release(version='1.2.0', ext='deb', arch='x64', **changes):
    name = 'Everywhere-Linux-%s-v%s.%s' % (arch, version, ext)
    result = {'tag_name': 'v' + version, 'draft': False, 'prerelease': '-canary.' in version,
              'assets': [{'name': name, 'state': 'uploaded', 'size': len(PAYLOAD),
                          'digest': 'sha256:' + hashlib.sha256(PAYLOAD).hexdigest(),
                          'browser_download_url': 'https://github.com/pasical/Everywhere/releases/download/v%s/%s' % (version, name)}]}
    result.update(changes)
    return result


class SelectionTests(unittest.TestCase):
    def select(self, items, channel='stable', arch='x64', ext='deb'):
        return u.select_release(items, channel, arch, ext, 'pasical/Everywhere')

    def test_numeric_version_order(self):
        self.assertEqual(self.select([release('1.9.0'), release('1.10.0')])[0], '1.10.0')

    def test_stable_excludes_canary_and_draft(self):
        self.assertEqual(self.select([release(), release('2.0.0-canary.1'), release('3.0.0', draft=True)])[0], '1.2.0')

    def test_canary_numeric_order(self):
        self.assertEqual(self.select([release('2.0.0-canary.9'), release('2.0.0-canary.10')], 'canary')[0], '2.0.0-canary.10')

    def test_stable_after_canary(self):
        self.assertGreater(u.version_key('1.0.0'), u.version_key('1.0.0-canary.99'))

    def test_no_silent_fallback_to_older_complete_release(self):
        with self.assertRaises(u.UpdateError):
            self.select([release(), release('2.0.0', assets=[])])

    def test_missing_or_duplicate_asset(self):
        for assets in ([], release()['assets'] * 2):
            with self.subTest(assets=assets), self.assertRaises(u.UpdateError):
                self.select([release(assets=assets)])

    def test_bad_digest_size_url_and_state(self):
        for key, value in [('digest', None), ('digest', 'sha256:123'), ('size', 0), ('size', True),
                           ('browser_download_url', 'https://attacker.invalid/evil.deb'), ('state', 'new')]:
            item = release()
            item['assets'][0][key] = value
            with self.subTest(key=key), self.assertRaises(u.UpdateError):
                self.select([item])

    def test_arm_rpm_selection(self):
        self.assertEqual(self.select([release(ext='rpm', arch='arm64')], arch='arm64', ext='rpm')[0], '1.2.0')

    def test_channel_flag_mismatch(self):
        with self.assertRaises(u.UpdateError):
            self.select([release(prerelease=True)])

    def test_unsupported_versions_and_architectures(self):
        for value in ['../bad', '1.0.0;touch /tmp/bad', '1.0.0-beta.1']:
            with self.assertRaises(u.UpdateError):
                u.version_key(value)
        with self.assertRaises(u.UpdateError):
            u.architecture('i686')

    def test_digest_and_size_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'package'
            asset = release()['assets'][0]
            path.write_bytes(PAYLOAD)
            u.verify_file(path, asset)
            for body in [b'bad', b'x' * len(PAYLOAD)]:
                path.write_bytes(body)
                with self.assertRaises(u.UpdateError):
                    u.verify_file(path, asset)

    def test_http_and_redirect_downgrade_rejected(self):
        with self.assertRaises(u.UpdateError):
            u.fetch('http://example.com/pkg')
        with self.assertRaises(u.UpdateError):
            u.HTTPSRedirect().redirect_request(None, None, 302, '', {}, 'http://example.com/pkg')


class RoutingTests(unittest.TestCase):
    def detect(self, distro, responses, tools):
        with patch.object(u, 'probe', side_effect=lambda *args: responses.get(args, '')), \
             patch.object(u.shutil, 'which', side_effect=lambda name: '/bin/' + name if name in tools else None), \
             patch.object(u.Path, 'exists', return_value=False):
            return u.detect(distro)

    def test_debian_derivatives_use_installed_owner(self):
        for distro in ['debian', 'ubuntu', 'linuxmint', 'pop', 'kali', 'unknown']:
            with self.subTest(distro=distro):
                result = self.detect({'ID': distro}, {('dpkg-query', '-W', '-f=${Status}\t${Version}\t${Architecture}', 'everywhere'): 'install ok installed\t1.0.0\tamd64'}, {'apt-get'})
                self.assertEqual(result[0], 'apt-get')

    def test_rpm_managers(self):
        for distro, tools, expected in [('fedora', {'dnf', 'yum'}, 'dnf'), ('rocky', {'dnf'}, 'dnf'), ('centos', {'yum'}, 'yum'), ('opensuse-tumbleweed', {'zypper', 'dnf'}, 'zypper'), ('derivative', {'zypper'}, 'zypper')]:
            with self.subTest(distro=distro):
                result = self.detect({'ID': distro, 'ID_LIKE': 'suse' if distro == 'derivative' else ''}, {('rpm', '-q', '--qf', '%{VERSION}\t%{ARCH}', 'everywhere'): '1.0.0\tx86_64'}, tools)
                self.assertEqual(result[0], expected)

    def test_aur_helpers(self):
        for helper in ['yay', 'paru']:
            self.assertEqual(self.detect({'ID': 'arch'}, {('pacman', '-Q', 'everywhere-bin'): 'everywhere-bin 1.0.0-1'}, {helper})[0], helper)

    def test_repo_owned_arch_package(self):
        result = self.detect({'ID': 'arch'}, {('pacman', '-Q', 'everywhere'): 'everywhere 1.0.0-1', ('pacman', '-Sl'): 'extra everywhere 1.0.0-1 [installed]'}, {'pacman'})
        self.assertEqual(result[0], 'pacman')

    def test_unsupported_systems_and_missing_installation(self):
        for distro in ['nixos', 'alpine', 'bazzite', 'microos', 'debian']:
            with self.subTest(distro=distro), self.assertRaises(u.UpdateError):
                self.detect({'ID': distro}, {}, set())

    def test_ambiguous_installation_rejected(self):
        responses = {('rpm', '-q', '--qf', '%{VERSION}\t%{ARCH}', 'everywhere'): '1.0.0\tx86_64', ('pacman', '-Q', 'everywhere-bin'): 'everywhere-bin 1.0.0-1'}
        with self.assertRaises(u.UpdateError):
            self.detect({'ID': 'fedora'}, responses, {'dnf', 'yay'})

    def test_commands_preserve_argument_boundaries(self):
        path = '/tmp/path with spaces/package.rpm'
        for manager in ['apt-get', 'dnf', 'yum', 'zypper']:
            cmd = u.command(manager, path)
            self.assertEqual(cmd[-1], path)
            self.assertNotIn('--nogpgcheck', cmd)
        self.assertEqual(u.command('yay', 'everywhere-bin'), ['yay', '-Syu', 'everywhere-bin'])

    def test_package_metadata_mismatch(self):
        with patch.object(u, 'probe', return_value='wrong'), self.assertRaises(u.UpdateError):
            u.validate_package(Path('/tmp/mock.deb'), 'apt-get', '1.2.0', 'x64')


class FlowTests(unittest.TestCase):
    def run_flow(self, args, current='1.0.0', manager='apt-get', after='1.2.0', bad_download=False, transaction_error=False):
        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            fixture = Path(directory) / 'release.json'
            fixture.write_text(json.dumps([release(ext='deb' if manager == 'apt-get' else 'rpm')]))
            stack.enter_context(patch.object(u.platform, 'system', return_value='Linux'))
            stack.enter_context(patch.object(u.platform, 'machine', return_value='x86_64'))
            stack.enter_context(patch.object(u, 'distro_info', return_value={'ID': 'ubuntu'}))
            arch = 'amd64' if manager == 'apt-get' else 'x86_64'
            installed = (manager, current, arch, 'everywhere')
            detect = stack.enter_context(patch.object(u, 'detect', side_effect=[installed, installed, (manager, after, arch, 'everywhere')]))
            stack.enter_context(patch.object(u, 'lock', side_effect=contextlib.nullcontext))
            validate = stack.enter_context(patch.object(u, 'validate_package'))
            execute = stack.enter_context(patch.object(u, 'execute', side_effect=subprocess.CalledProcessError(7, 'package-manager') if transaction_error else None))
            def download(url, path, size):
                path.write_bytes(b'bad' if bad_download else PAYLOAD)
            fetch = stack.enter_context(patch.object(u, 'fetch', side_effect=download))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            result = u.main(['--release-json', str(fixture)] + args)
            return result, fetch.call_count, validate.call_count, execute.call_count, detect.call_count

    def test_preview_has_no_download_or_transaction(self):
        self.assertEqual(self.run_flow([]), (0, 0, 0, 0, 1))

    def test_saved_channel_is_used_by_default(self):
        with patch.dict(os.environ, {'EVERYWHERE_UPDATE_CHANNEL': 'canary'}):
            with self.assertRaises(u.UpdateError):
                self.run_flow([])

    def test_explicit_channel_overrides_saved_channel(self):
        with patch.dict(os.environ, {'EVERYWHERE_UPDATE_CHANNEL': 'canary'}):
            self.assertEqual(self.run_flow(['--channel', 'stable'])[1:4], (0, 0, 0))

    def test_fixed_target_matches_requested_release(self):
        self.assertEqual(self.run_flow(['--version', '1.2.0'])[1:4], (0, 0, 0))

    def test_unavailable_fixed_target_does_not_fall_back(self):
        with self.assertRaises(u.UpdateError):
            self.run_flow(['--apply', '--version', '1.1.0'])

    def test_dry_run_has_no_transaction(self):
        self.assertEqual(self.run_flow(['--dry-run'])[1:4], (0, 0, 0))

    def test_equal_or_newer_installation_is_noop(self):
        for current in ['1.2.0', '2.0.0']:
            self.assertEqual(self.run_flow(['--apply'], current=current)[1:4], (0, 0, 0))

    def test_all_native_apply_paths(self):
        for manager in ['apt-get', 'dnf', 'yum', 'zypper']:
            with self.subTest(manager=manager):
                self.assertEqual(self.run_flow(['--apply'], manager=manager), (0, 1, 1, 1, 3))

    def test_download_failure(self):
        with self.assertRaises(u.UpdateError):
            self.run_flow(['--apply'], bad_download=True)

    def test_package_manager_failure_propagates(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_flow(['--apply'], transaction_error=True)

    def test_false_success_rejected(self):
        with self.assertRaises(u.UpdateError):
            self.run_flow(['--apply'], after='1.0.0')

    def test_arch_requires_explicit_system_upgrade(self):
        with patch.object(u.platform, 'system', return_value='Linux'), patch.object(u.platform, 'machine', return_value='x86_64'), patch.object(u, 'distro_info', return_value={}), patch.object(u, 'detect', return_value=('yay', '1.0-1', '', 'everywhere-bin')), patch.object(u, 'execute') as execute, contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(u.UpdateError):
                u.main(['--apply'])
            execute.assert_not_called()

    def test_os_release_not_executed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'os-release'
            marker = Path(directory) / 'executed'
            path.write_text('ID="ubuntu"\nNAME="$(touch %s)"\n' % marker)
            self.assertEqual(u.distro_info(path)['ID'], 'ubuntu')
            self.assertFalse(marker.exists())

    def test_lock_excludes_concurrent_updater_and_releases(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(u.Path, 'home', return_value=Path(directory)):
            with u.lock():
                with self.assertRaises(u.UpdateError):
                    with u.lock():
                        self.fail('second updater acquired lock')
            with u.lock():
                pass


if __name__ == '__main__':
    unittest.main()
