#!/usr/bin/env python3
"""Standalone Everywhere Linux package updater (Python 3.9+, standard library)."""
import argparse
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


class UpdateError(Exception):
    pass


def probe(*args):
    if not shutil.which(args[0]):
        return ""
    result = subprocess.run(args, text=True, capture_output=True, timeout=30, env=dict(os.environ, LC_ALL='C'))
    return result.stdout.strip() if result.returncode == 0 else ""


def distro_info(path=Path('/etc/os-release')):
    result = {}
    for line in path.read_text().splitlines():
        if '=' in line and not line.startswith('#'):
            key, value = line.split('=', 1)
            # Never source os-release as executable shell code.
            result[key] = value.strip().strip('\"\'')
    return result


def architecture(machine):
    try:
        return {'x86_64': 'x64', 'aarch64': 'arm64', 'arm64': 'arm64'}[machine]
    except KeyError:
        raise UpdateError('Only Linux x86_64 and aarch64 assets are published.')


def detect(info):
    ids = set((info.get('ID', '') + ' ' + info.get('ID_LIKE', '')).split())
    if 'nixos' in ids:
        raise UpdateError('NixOS: update the owning flake/profile and rebuild; do not overwrite the Nix store.')
    if Path('/run/ostree-booted').exists() or ids & {'bazzite', 'silverblue', 'kinoite', 'aeon', 'microos'}:
        raise UpdateError('Immutable system: use its image/transactional update workflow.')
    if 'alpine' in ids:
        raise UpdateError('Alpine/musl has no compatible published Everywhere package.')
    found = []
    deb = probe('dpkg-query', '-W', '-f=${Status}\t${Version}\t${Architecture}', 'everywhere')
    if deb.startswith('install ok installed\t'):
        _, version, arch = deb.split('\t')
        found.append(('apt-get', version, arch, 'everywhere'))
    rpm = probe('rpm', '-q', '--qf', '%{VERSION}\t%{ARCH}', 'everywhere')
    if rpm:
        version, arch = rpm.split('\t')
        choices = ('zypper',) if ids & {'suse', 'opensuse', 'opensuse-leap', 'opensuse-tumbleweed', 'sles'} else ('dnf', 'yum')
        manager = next((p for p in choices if shutil.which(p)), None)
        if not manager:
            raise UpdateError('RPM installation found, but no supported dependency-resolving package manager.')
        found.append((manager, version, arch, 'everywhere'))
    for name in ('everywhere-bin', 'everywhere'):
        arch_pkg = probe('pacman', '-Q', name)
        if arch_pkg:
            manager = 'pacman' if probe('pacman', '-Sl').find(' ' + name + ' ') >= 0 else next((p for p in ('paru', 'yay') if shutil.which(p)), '')
            if not manager:
                raise UpdateError('External pacman package found. Use its original package source or update its reviewed PKGBUILD manually.')
            found.append((manager, arch_pkg.split()[1], '', name))
    if len(found) != 1:
        raise UpdateError('Expected exactly one native Everywhere installation; found %d. Portable/Flatpak/Snap installs must use their own update source.' % len(found))
    if not shutil.which(found[0][0]):
        raise UpdateError('Required package manager is missing: ' + found[0][0])
    return found[0]


def version_key(value):
    match = re.fullmatch(r'v?(\d+)\.(\d+)\.(\d+)(?:-canary\.([0-9]+(?:\.[0-9]+)*))?', value)
    if not match:
        raise UpdateError('Unsupported release version: ' + value)
    major, minor, patch, canary = match.groups()
    return (int(major), int(minor), int(patch), canary is None,
            tuple(map(int, canary.split('.'))) if canary else ())


def installed_version(value):
    # DEB/RPM packages produced by this fork use ~canary. to sort below stable.
    value = value.replace('~canary.', '-canary.')
    version_key(value)
    return value


class HTTPSRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != 'https':
            raise UpdateError('Refusing a non-HTTPS redirect.')
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def fetch(url, destination=None, limit=8 * 1024 * 1024):
    if urllib.parse.urlsplit(url).scheme != 'https':
        raise UpdateError('Only HTTPS downloads are allowed.')
    transport = os.environ.get('EVERYWHERE_UPDATE_TRANSPORT')
    if transport:
        command_line = json.loads(transport)
        if not isinstance(command_line, list) or not command_line or not all(isinstance(part, str) for part in command_line):
            raise UpdateError('Invalid application download transport.')
        with tempfile.TemporaryDirectory(prefix='everywhere-response-') as directory:
            output = Path(destination) if destination else Path(directory) / 'response'
            for attempt in range(3):
                result = subprocess.run(command_line + [url, str(output), str(limit)], timeout=200)
                if result.returncode == 0:
                    if not output.is_file() or output.stat().st_size > limit:
                        raise UpdateError('Application transport returned an invalid response.')
                    return b'' if destination else output.read_bytes()
                if attempt < 2:
                    time.sleep(attempt + 1)
            raise UpdateError('Download failed using the application network settings.')
    opener = urllib.request.build_opener(HTTPSRedirect())
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'Everywhere-Linux-Updater', 'Accept': 'application/vnd.github+json'})
            with opener.open(req, timeout=30) as response:
                with (open(destination, 'wb') if destination else contextlib.nullcontext()) as output:
                    chunks, count = [], 0
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        count += len(chunk)
                        if count > limit:
                            raise UpdateError('Response exceeds expected size limit.')
                        if output:
                            output.write(chunk)
                        else:
                            chunks.append(chunk)
                    return b''.join(chunks)
        except urllib.error.HTTPError as exc:
            if exc.code not in (429, 500, 502, 503, 504) or attempt == 2:
                raise UpdateError('Download HTTP %s: %s' % (exc.code, url)) from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            if attempt == 2:
                raise UpdateError('Download failed: ' + str(exc)) from exc
        time.sleep(attempt + 1)


def releases(repo):
    items = []
    for page in range(1, 11):
        batch = json.loads(fetch('https://api.github.com/repos/%s/releases?per_page=100&page=%d' % (repo, page)))
        if not isinstance(batch, list):
            raise UpdateError('Invalid release list.')
        items.extend(batch)
        if len(batch) < 100:
            return items
    raise UpdateError('Release history exceeds scan limit; refusing an incomplete selection.')


def select_release(items, channel, arch, extension, repo):
    candidates = []
    for release in items:
        if release.get('draft'):
            continue
        try:
            key = version_key(release.get('tag_name', ''))
        except UpdateError:
            continue
        if release.get('prerelease') != (not key[3]):
            continue
        if (channel == 'stable') != key[3]:
            continue
        candidates.append((key, release))
    if not candidates:
        raise UpdateError('No published release in channel ' + channel)
    _, release = max(candidates, key=lambda item: item[0])
    tag = release['tag_name']
    name = 'Everywhere-Linux-%s-v%s.%s' % (arch, tag.removeprefix('v'), extension)
    assets = [a for a in release.get('assets', []) if a.get('name') == name and a.get('state') == 'uploaded']
    if len(assets) != 1:
        raise UpdateError('Latest release lacks the completed asset ' + name + '; retry after publishing finishes.')
    asset = assets[0]
    if not re.fullmatch(r'sha256:[0-9a-fA-F]{64}', asset.get('digest') or ''):
        raise UpdateError('Asset lacks a SHA256 digest; publisher must provide one. Verification cannot be skipped.')
    if type(asset.get('size')) is not int or not 0 < asset['size'] <= 2 * 1024**3:
        raise UpdateError('Invalid package size.')
    expected = 'https://github.com/%s/releases/download/%s/%s' % (repo, tag, name)
    if asset.get('browser_download_url') != expected:
        raise UpdateError('Asset URL does not match the selected repository, tag and filename.')
    return tag.removeprefix('v'), asset


def verify_file(path, asset):
    if path.stat().st_size != asset['size']:
        raise UpdateError('Downloaded package size mismatch.')
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    if digest.hexdigest() != asset['digest'].split(':')[1].lower():
        raise UpdateError('Downloaded package SHA256 mismatch.')


def validate_package(path, manager, target, arch):
    expected_arch = {'x64': 'amd64', 'arm64': 'arm64'}[arch] if manager == 'apt-get' else {'x64': 'x86_64', 'arm64': 'aarch64'}[arch]
    if manager == 'apt-get':
        values = [probe('dpkg-deb', '-f', str(path), key) for key in ('Package', 'Version', 'Architecture')]
    else:
        values = probe('rpm', '-qp', '--qf', '%{NAME}\t%{VERSION}\t%{ARCH}', str(path)).split('\t')
    if values != ['everywhere', target.replace('-canary.', '~canary.'), expected_arch]:
        raise UpdateError('Package identity/version/architecture differs from release metadata.')


def command(manager, package, yes=False):
    if manager == 'apt-get':
        return [manager, '--no-remove'] + (['-y'] if yes else []) + ['install', str(package)]
    if manager in ('dnf', 'yum'):
        return [manager] + (['-y'] if yes else []) + ['upgrade', str(package)]
    if manager == 'zypper':
        return [manager] + (['--non-interactive'] if yes else []) + ['install', str(package)]
    # Arch requires a full system upgrade, never pacman -Sy <package>.
    return [manager, '-Syu', str(package)]


def execute(args, privileged):
    if privileged and os.geteuid() != 0:
        if not shutil.which('sudo'):
            raise UpdateError('sudo is required for the package transaction.')
        args = ['sudo', '--'] + args
    print('+ ' + shlex.join(args), flush=True)
    subprocess.run(args, check=True)


@contextlib.contextmanager
def lock():
    # Per-user flock serializes this updater; native managers retain their own global locks.
    directory = Path.home() / '.cache' / 'everywhere-updater'
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    if directory.is_symlink() or directory.stat().st_uid != os.getuid() or directory.stat().st_mode & 0o022:
        raise UpdateError('Updater cache directory has unsafe ownership or permissions.')
    fd = os.open(directory / 'update.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise UpdateError('Another updater is already running.')
        yield
    finally:
        os.close(fd)


def main(argv=None):
    parser = argparse.ArgumentParser(prog='ew update', description='Update Everywhere using its installed package manager.')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply', action='store_true', help='download, verify and install the upgrade')
    mode.add_argument('--dry-run', action='store_true', help='preview without downloading or installing')
    parser.add_argument('--version', help='require this exact release; never downgrade')
    parser.add_argument('--yes', action='store_true', help='accept native DEB/RPM transaction prompts')
    parser.add_argument('--repo', default='pasical/Everywhere', help='explicit trusted GitHub owner/repo')
    parser.add_argument('--channel', choices=('auto', 'stable', 'canary'), default=os.environ.get('EVERYWHERE_UPDATE_CHANNEL', 'auto'), help='override the saved application channel')
    parser.add_argument('--proxy', help='override the application proxy: system, none, or an HTTP/HTTPS/SOCKS5 URL')
    parser.add_argument('--allow-system-upgrade', action='store_true', help='allow Arch full-system upgrade')
    parser.add_argument('--release-json', type=Path, help='use a trusted local GitHub release-list fixture instead of the API')
    args = parser.parse_args(argv)
    if args.proxy is not None:
        if not os.environ.get('EVERYWHERE_UPDATE_TRANSPORT'):
            raise UpdateError('Proxy overrides require the ew update application entry point.')
        os.environ['EVERYWHERE_UPDATE_PROXY'] = args.proxy
    if args.channel not in ('auto', 'stable', 'canary'):
        raise UpdateError('Invalid application update channel.')
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repo):
        raise UpdateError('Invalid GitHub repository.')
    if platform.system() != 'Linux':
        raise UpdateError('This updater runs on Linux only; run the isolated tests on other hosts.')
    arch = architecture(platform.machine())
    manager, current, package_arch, name = detect(distro_info())
    print('Installed: %s %s; manager: %s' % (name, current, manager), flush=True)
    if manager in ('pacman', 'paru', 'yay'):
        if args.release_json or args.version:
            raise UpdateError('Arch fixed versions are controlled by the owning repository/AUR recipe.')
        if args.channel != 'auto':
            info = probe(manager, '-Si', name)
            match = re.search(r'^Version\s*:\s*(\S+)', info, re.MULTILINE)
            if not match:
                raise UpdateError('Cannot verify the repository/AUR candidate against your selected channel.')
            offered_channel = 'canary' if 'canary' in match.group(1).lower() else 'stable'
            if offered_channel != args.channel:
                raise UpdateError('Your selected channel is %s, but %s currently offers %s. Choose a matching package source; settings will not be overridden.' % (args.channel, name, offered_channel))
        cmd = command(manager, name)
        print('Arch/AUR follows its recipe policy (everywhere-bin currently includes Canary).')
        print('Plan (full system upgrade): ' + shlex.join(cmd))
        if not args.apply:
            return 0
        if not args.allow_system_upgrade:
            if not sys.stdin.isatty():
                raise UpdateError('Arch requires an interactive terminal for full-system upgrade consent, or --allow-system-upgrade.')
            if input('This also upgrades other system packages. Continue? [y/N] ').strip().lower() not in ('y', 'yes'):
                raise UpdateError('System upgrade canceled; no changes made.')
        if args.yes:
            raise UpdateError('Unattended AUR/Arch upgrades are not enabled; review package/build prompts.')
        if manager != 'pacman' and os.geteuid() == 0:
            raise UpdateError('Run AUR helpers as a normal user, without sudo.')
        with lock():
            execute(cmd, manager == 'pacman')
        print('Package manager completed. Restart Everywhere.')
        return 0
    expected_arch = {'apt-get': {'x64': 'amd64', 'arm64': 'arm64'}}.get(manager, {'x64': 'x86_64', 'arm64': 'aarch64'})[arch]
    if package_arch != expected_arch:
        raise UpdateError('Installed package architecture does not match this machine.')
    current = installed_version(current)
    channel = args.channel if args.channel != 'auto' else ('canary' if '-canary.' in current else 'stable')
    items = json.loads(args.release_json.read_text()) if args.release_json else releases(args.repo)
    if not isinstance(items, list):
        raise UpdateError('Release metadata must be a JSON array.')
    if args.version:
        version_key(args.version)
        items = [item for item in items if item.get('tag_name') == 'v' + args.version.removeprefix('v')]
    target, asset = select_release(items, channel, arch, 'deb' if manager == 'apt-get' else 'rpm', args.repo)
    if version_key(target) <= version_key(current):
        print('No newer compatible release; no downgrade or reinstall performed.')
        return 0
    print('Source: %s; channel: %s; target: %s' % (args.repo, channel, target))
    print('Plan: ' + shlex.join(command(manager, '/temporary-download/' + asset['name'], args.yes)))
    if not args.apply:
        print('Preview only. Use --apply to download, verify and upgrade.')
        return 0
    with lock(), tempfile.TemporaryDirectory(prefix='everywhere-update-') as temp:
        path = Path(temp) / asset['name']
        fetch(asset['browser_download_url'], path, asset['size'])
        verify_file(path, asset)
        validate_package(path, manager, target, arch)
        # Recheck after the download in case another package transaction changed the installation.
        if detect(distro_info()) != (manager, current.replace('-canary.', '~canary.'), package_arch, name):
            raise UpdateError('Installation changed during download; rerun the updater.')
        execute(command(manager, path, args.yes), True)
        after = detect(distro_info())
        if after[0] != manager or installed_version(after[1]) != target:
            raise UpdateError('Package manager returned success but target version is not installed.')
    print('Upgrade verified. Restart Everywhere to use the new version.')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (UpdateError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as exc:
        print('Update failed: ' + str(exc), file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        print('Update interrupted. Check the package manager state before retrying.', file=sys.stderr)
        sys.exit(130)
