#!/usr/bin/env python3
"""Independent Everywhere Linux installer (Python 3.9+, standard library)."""
import argparse
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import select
import termios
import tty
import tarfile


class UpdateError(Exception):
    pass


class DownloadError(UpdateError):
    """A transport failure, distinct from rejected or corrupted metadata."""

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
            raise DownloadError('Download failed using the application network settings.')
    opener = urllib.request.build_opener(HTTPSRedirect())
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={
                'User-Agent': 'Everywhere-Linux-Installer', 'Accept': 'application/vnd.github+json'})
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
                raise DownloadError('Download HTTP %s: %s' % (exc.code, url)) from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            if attempt == 2:
                raise DownloadError('Download failed: ' + str(exc)) from exc
        time.sleep(attempt + 1)


INSTALLER_VERSION = '0.1.0'
INSTALLER_URL = 'https://github.com/pasical/Everywhere/releases/download/linux-installer-latest/installer.json'
CATALOG_URL = 'github:pasical/Everywhere'


def version_key(value):
    if not isinstance(value, str):
        raise UpdateError('Expected a version string.')
    match = re.fullmatch(r'v?(\d+)\.(\d+)\.(\d+)(?:-canary\.([0-9]+(?:\.[0-9]+)*))?', value)
    if not match:
        raise UpdateError('Unsupported version: ' + str(value))
    major, minor, patch, canary = match.groups()
    return (int(major), int(minor), int(patch), canary is None,
            tuple(map(int, canary.split('.'))) if canary else ())


def read_json(source):
    # An explicit local manifest is useful for staging; production uses HTTPS only.
    if source.startswith('https://'):
        return json.loads(fetch(source))
    path = Path(source)
    if not path.is_absolute():
        raise UpdateError('Manifest must be an HTTPS URL or an explicit absolute file path.')
    return json.loads(path.read_text())


def validate_asset(asset):
    if (not isinstance(asset, dict) or
            type(asset.get('size')) is not int or not 0 < asset['size'] <= 2 * 1024**3 or
            not isinstance(asset.get('sha256'), str) or
            not re.fullmatch(r'[0-9a-f]{64}', asset['sha256']) or
            not isinstance(asset.get('url'), str) or
            urllib.parse.urlsplit(asset.get('url', '')).scheme != 'https'):
        raise UpdateError('Invalid download metadata.')


def verify_file(path, asset):
    validate_asset(asset)
    with path.open('rb') as stream:
        digest = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    if path.stat().st_size != asset['size'] or digest.hexdigest() != asset['sha256']:
        raise UpdateError('Download size or SHA256 mismatch.')


def refresh_installer(source, arguments):
    """Execute a verified newer script without changing package-owned files."""
    try:
        manifest = read_json(source)
    except (DownloadError, urllib.error.URLError, OSError):
        print('Could not check installer updates; using bundled installer %s.' % INSTALLER_VERSION)
        return None
    if not isinstance(manifest, dict) or manifest.get('schema') != 1 or manifest.get('protocol') != 1:
        raise UpdateError('Unsupported installer protocol.')
    version = manifest['version']
    if version_key(version) <= version_key(INSTALLER_VERSION):
        return None
    asset = manifest['asset']
    validate_asset(asset)
    if asset['size'] > 4 * 1024 * 1024:
        raise UpdateError('Installer is too large.')
    cache = Path.home() / '.cache' / 'everywhere-installer'
    cache.mkdir(parents=True, exist_ok=True, mode=0o700)
    if cache.is_symlink() or cache.stat().st_uid != os.getuid() or cache.stat().st_mode & 0o077:
        raise UpdateError('Installer cache must be private and owned by the current user.')
    script = cache / (asset['sha256'] + '.py')
    if script.is_symlink():
        raise UpdateError('Unsafe installer cache entry.')
    if script.exists():
        verify_file(script, asset)
    else:
        with tempfile.TemporaryDirectory(prefix='download-', dir=cache) as directory:
            download = Path(directory) / 'installer.py'
            fetch(asset['url'], download, asset['size'])
            verify_file(download, asset)
            os.replace(download, script)
    print('Using installer %s.' % version, flush=True)
    return subprocess.run([sys.executable, str(script), '--no-installer-update', *arguments], check=False).returncode


def choose_channel():
    """Read the controlling terminal, including when stdin contains a curl script."""
    try:
        terminal = open('/dev/tty', 'r+b', buffering=0)
    except OSError as exc:
        raise UpdateError('An interactive terminal is required. Specify --channel stable or --channel canary.') from exc
    with terminal:
        fd = terminal.fileno()
        previous = termios.tcgetattr(fd)
        selected = 0
        terminal.write(b'Everywhere\r\n\r\nSelect a release channel\r\n\r\n')
        try:
            tty.setraw(fd)
            terminal.write(b'\x1b[?25l')
            while True:
                labels = ['Stable', 'Canary']
                text = ('\r\x1b[2K%s Stable\r\n\x1b[2K%s Canary\r\n\r\n'
                        '\x1b[2KUp/Down Navigate | Enter Select | Esc Cancel') % (
                    '>' if selected == 0 else ' ', '>' if selected == 1 else ' ')
                terminal.write(text.encode())
                key = terminal.read(1)
                if key in (b'\r', b'\n'):
                    terminal.write(('\r\n\r\nSelected: %s\r\n' % labels[selected]).encode())
                    return labels[selected].lower()
                if key in (b'\x03', b'\x04', b''):
                    raise KeyboardInterrupt
                if key == b'\x1b':
                    sequence = b''
                    while len(sequence) < 2 and select.select([fd], [], [], 0.1)[0]:
                        sequence += terminal.read(1)
                    if sequence in (b'[A', b'OA'):
                        selected = (selected - 1) % 2
                    elif sequence in (b'[B', b'OB'):
                        selected = (selected + 1) % 2
                    elif not sequence:
                        raise KeyboardInterrupt
                terminal.write(b'\x1b[3A')
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, previous)
            terminal.write(b'\x1b[?25h\r\n')


def backend(info):
    """Keep system support in the independently released installer."""
    distro = info.get('ID', '')
    version = info.get('VERSION_ID', '')
    if Path('/run/ostree-booted').exists():
        raise UpdateError('This immutable system is not supported.')
    if distro in ('arch', 'endeavouros', 'manjaro'):
        manager = 'pacman'
    elif distro == 'ubuntu' and version in ('24.04', '25.10'):
        manager = 'apt-get'
    elif distro == 'debian' and version in ('13', '14'):
        manager = 'apt-get'
    elif distro == 'fedora' and version in ('44',):
        manager = 'dnf'
    else:
        raise UpdateError('Unsupported system: %s %s.' % (distro, version))
    if not shutil.which(manager):
        raise UpdateError('Required system tool is missing: ' + manager)
    return manager


def installed(manager):
    if manager == 'apt-get':
        value = probe('dpkg-query', '-W', '-f=${Status}\t${Version}', 'everywhere')
        return ('everywhere', value.split('\t')[1]) if value.startswith('install ok installed\t') else None
    if manager == 'dnf':
        value = probe('rpm', '-q', '--qf', '%{VERSION}', 'everywhere')
        return ('everywhere', value) if value else None
    matches = []
    for name in ('everywhere-bin', 'everywhere'):
        value = probe('pacman', '-Q', name)
        if value:
            actual_name, version = value.split()
            if actual_name not in ('everywhere-bin', 'everywhere'):
                raise UpdateError('Unexpected installed package identity.')
            entry = (actual_name, version)
            if entry not in matches:
                matches.append(entry)
    if len(matches) > 1:
        raise UpdateError('Multiple Everywhere packages are installed.')
    return matches[0] if matches else None


def normalize_version(value, manager):
    if manager == 'pacman':
        # Epoch controls package ordering after AUR withdrawal; it is not an app version.
        value = value.split(':', 1)[-1]
        value = value.rsplit('-', 1)[0]
        value = re.sub(r'^(\d+\.\d+\.\d+)canary\.', r'\1-canary.', value)
    return value.replace('~canary.', '-canary.')


def read_catalog(source):
    if not source.startswith('github:'):
        return read_json(source)
    repo = source.removeprefix('github:')
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
        raise UpdateError('Invalid release repository.')
    releases = []
    for page in range(1, 11):
        batch = json.loads(fetch('https://api.github.com/repos/%s/releases?per_page=100&page=%d' % (repo, page)))
        if not isinstance(batch, list):
            raise UpdateError('Invalid release response.')
        releases.extend(batch)
        if len(batch) < 100:
            break
    else:
        raise UpdateError('Release history exceeds the complete scan limit.')
    channels = {}
    for release in releases:
        if not isinstance(release, dict):
            raise UpdateError('Expected a release object.')
        if release.get('draft'):
            continue
        try:
            version = release.get('tag_name', '').removeprefix('v')
            key = version_key(version)
        except UpdateError:
            continue
        if release.get('prerelease') != (not key[3]):
            continue
        channel = 'stable' if key[3] else 'canary'
        if channel in channels and version_key(channels[channel]['version']) >= key:
            continue
        assets = {}
        for arch in ('x64', 'arm64'):
            assets[arch] = {}
            for extension in ('deb', 'rpm', 'tar.zst'):
                name = 'Everywhere-Linux-%s-v%s.%s' % (arch, version, extension)
                matches = [item for item in release.get('assets', [])
                           if item.get('name') == name and item.get('state') == 'uploaded']
                if len(matches) != 1:
                    continue
                item = matches[0]
                expected = 'https://github.com/%s/releases/download/%s/%s' % (repo, release['tag_name'], name)
                digest = item.get('digest') or ''
                if item.get('browser_download_url') != expected or not digest.startswith('sha256:'):
                    continue
                assets[arch][extension] = {'url': expected, 'size': item['size'], 'sha256': digest[7:]}
        resources = {}
        for label, filename in (('icon', 'Everywhere-icon.png'), ('license', 'LICENSE')):
            for item in release.get('assets', []):
                if item.get('name') == filename and item.get('state') == 'uploaded':
                    expected = 'https://github.com/%s/releases/download/%s/%s' % (repo, release['tag_name'], filename)
                    digest = item.get('digest') or ''
                    if item.get('browser_download_url') == expected and digest.startswith('sha256:'):
                        resources[label] = {'url': expected, 'size': item['size'], 'sha256': digest[7:]}
        for entries in assets.values():
            if 'tar.zst' in entries:
                entries['tar.zst']['resources'] = resources
        channels[channel] = {'version': version, 'assets': assets}
    return {'schema': 1, 'channels': channels}


def select_package(catalog, channel, arch, manager):
    if not isinstance(catalog, dict) or catalog.get('schema') != 1:
        raise UpdateError('Unsupported download catalog.')
    channels = catalog.get('channels')
    if not isinstance(channels, dict):
        raise UpdateError('Expected release channels.')
    release = channels.get(channel)
    if not release:
        raise UpdateError('No Linux release is available for ' + channel + '.')
    if not isinstance(release, dict):
        raise UpdateError('Expected a channel release object.')
    target = release['version']
    if ('canary' if not version_key(target)[3] else 'stable') != channel:
        raise UpdateError('Release channel and version disagree.')
    extension = {'apt-get': 'deb', 'dnf': 'rpm', 'pacman': 'tar.zst'}[manager]
    assets = release.get('assets')
    if not isinstance(assets, dict) or not isinstance(assets.get(arch, {}), dict):
        raise UpdateError('Expected release assets by architecture.')
    asset = assets.get(arch, {}).get(extension)
    if not asset:
        raise UpdateError('No supported Linux download is available for this system.')
    validate_asset(asset)
    return target, asset


def prepare_arch(archive, directory, target, name, bundled_script, epoch):
    """Repackage the official payload without running code from its archive."""
    if os.geteuid() == 0:
        raise UpdateError('Run the installer as a normal user, without sudo.')
    if not shutil.which('makepkg') or not shutil.which('zstd'):
        raise UpdateError('Arch packaging requires makepkg and zstd.')
    payload = directory / 'payload'
    payload.mkdir()
    # Stream through the system decompressor for Python 3.9 compatibility.
    with subprocess.Popen(['zstd', '-dc', str(archive)], stdout=subprocess.PIPE) as process:
        with tarfile.open(fileobj=process.stdout, mode='r|') as tar:
            expanded_size = 0
            for member in tar:
                relative = Path(member.name)
                if relative.is_absolute() or '..' in relative.parts:
                    raise UpdateError('Unsafe archive path.')
                destination = payload / relative
                if member.isdir():
                    destination.mkdir(parents=True, exist_ok=True)
                    continue
                if not member.isfile():
                    raise UpdateError('Unsupported archive entry; only regular files and directories are allowed.')
                expanded_size += member.size
                if expanded_size > 4 * 1024**3:
                    raise UpdateError('Expanded archive is too large.')
                destination.parent.mkdir(parents=True, exist_ok=True)
                with tar.extractfile(member) as source, destination.open('wb') as output:
                    shutil.copyfileobj(source, output)
                destination.chmod(0o755 if member.mode & 0o111 else 0o644)
        if process.wait() != 0:
            raise UpdateError('Archive decompression failed.')
    if not (payload / 'Everywhere').is_file() or not (payload / 'Everywhere.Watchdog').is_file():
        raise UpdateError('The download is not an Everywhere application archive.')
    # The new local package must retain a working, bundled installer.
    shutil.copyfile(bundled_script, payload / 'linux-update.py')
    pkgver = target.replace('-canary.', 'canary.')
    architecture_name = {'x64': 'x86_64', 'arm64': 'aarch64'}[architecture(platform.machine())]
    recipe = """pkgname=%s
pkgver=%s
pkgrel=1
epoch=%s
pkgdesc='Everywhere desktop assistant'
arch=('%s')
license=('BUSL-1.1')
provides=('everywhere')
conflicts=('everywhere')
depends=('python' 'glibc' 'gcc-libs' 'zlib' 'openssl' 'icu' 'fontconfig'
         'libx11' 'libice' 'libsm' 'libxext' 'libxcb' 'libxrandr' 'libxrender'
         'libxi' 'libxcursor' 'wayland' 'libxkbcommon' 'glib2' 'at-spi2-core' 'xdg-utils')
options=('!strip' '!debug')
package() {
    install -d "$pkgdir/opt/Everywhere" "$pkgdir/usr/bin"
    cp -a "$startdir/payload/." "$pkgdir/opt/Everywhere/"
    chmod 755 "$pkgdir/opt/Everywhere/Everywhere" "$pkgdir/opt/Everywhere/Everywhere.Watchdog"
    ln -s /opt/Everywhere/Everywhere "$pkgdir/usr/bin/Everywhere"
    ln -s /opt/Everywhere/Everywhere "$pkgdir/usr/bin/ew"
    install -Dm644 "$startdir/icon" "$pkgdir/usr/share/icons/hicolor/256x256/apps/Everywhere.png"
    install -Dm644 "$startdir/license" "$pkgdir/usr/share/licenses/$pkgname/LICENSE"
    install -Dm644 "$startdir/com.Sylinko.Everywhere.desktop" "$pkgdir/usr/share/applications/com.Sylinko.Everywhere.desktop"
}
""" % (name, pkgver, epoch, architecture_name)
    (directory / 'PKGBUILD').write_text(recipe)
    (directory / 'com.Sylinko.Everywhere.desktop').write_text(
        '[Desktop Entry]\nName=Everywhere\nExec=/usr/bin/Everywhere %U\nType=Application\n'
        'Icon=Everywhere\nTerminal=false\nCategories=Utility;\nStartupWMClass=Everywhere\n')
    execute(['makepkg', '--syncdeps', '--cleanbuild', '--force'], False, cwd=directory)
    packages = [path for path in directory.glob('*.pkg.tar*') if not path.name.endswith('.sig')]
    if len(packages) != 1:
        raise UpdateError('Arch packaging did not produce exactly one package.')
    return packages[0]


def package_command(manager, path):
    if manager == 'apt-get':
        return [manager, '--no-remove', 'install', str(path)]
    if manager == 'dnf':
        return [manager, 'install', str(path)]
    return [manager, '-U', str(path)]


def validate_package(path, manager, target, arch):
    if manager == 'pacman':
        return  # The local recipe and verified archive determine identity.
    architectures = {'x64': 'amd64', 'arm64': 'arm64'} if manager == 'apt-get' else {
        'x64': 'x86_64', 'arm64': 'aarch64'}
    expected_arch = architectures[arch]
    if manager == 'apt-get':
        values = [probe('dpkg-deb', '-f', str(path), key) for key in ('Package', 'Version', 'Architecture')]
    else:
        values = probe('rpm', '-qp', '--qf', '%{NAME}\t%{VERSION}\t%{ARCH}', str(path)).split('\t')
    if values != ['everywhere', target.replace('-canary.', '~canary.'), expected_arch]:
        raise UpdateError('Package identity/version/architecture differs from download metadata.')


def execute(args, privileged, cwd=None):
    if privileged and os.geteuid() != 0:
        if not shutil.which('sudo'):
            raise UpdateError('sudo is required for the package transaction.')
        args = ['sudo', '--'] + args
    # curl | sh leaves stdin at EOF. Package-manager confirmation must use the
    # controlling terminal too, independently of the installer's menu input.
    terminal = None
    if not sys.stdin.isatty():
        try:
            terminal = open('/dev/tty', 'rb')
        except OSError:
            pass
    standard_input = terminal
    if terminal is None and not sys.stdin.isatty():
        standard_input = subprocess.DEVNULL
    with terminal if terminal is not None else contextlib.nullcontext():
        subprocess.run(args, check=True, stdin=standard_input, cwd=cwd,
                       env=dict(os.environ, LC_ALL='C'))


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


def main(arguments=None):
    parser = argparse.ArgumentParser(prog='Everywhere installer')
    parser.add_argument('action', nargs='?', choices=('install', 'update'), default='install')
    parser.add_argument('--channel', choices=('stable', 'canary', 'auto'),
                        default=os.environ.get('EVERYWHERE_UPDATE_CHANNEL', 'auto'))
    parser.add_argument('--dry-run', action='store_true')
    # Already distributed launchers pass --apply; installation is now the default.
    parser.add_argument('--apply', action='store_true', help=argparse.SUPPRESS)
    parser.add_argument('--no-installer-update', action='store_true', help='use the current verified installer')
    parser.add_argument('--installer-manifest', default=INSTALLER_URL)
    parser.add_argument('--catalog', default=CATALOG_URL)
    parser.add_argument('--proxy', help='override application proxy for this invocation')
    parser.add_argument('--installer-version', action='version', version=INSTALLER_VERSION)
    raw = list(sys.argv[1:] if arguments is None else arguments)
    args = parser.parse_args(raw)
    if platform.system() != 'Linux':
        raise UpdateError('This installer supports Linux only.')
    if args.proxy:
        if not os.environ.get('EVERYWHERE_UPDATE_TRANSPORT'):
            raise UpdateError('Use HTTPS_PROXY for standalone installation; --proxy requires ew update.')
        os.environ['EVERYWHERE_UPDATE_PROXY'] = args.proxy
    if not args.no_installer_update:
        result = refresh_installer(args.installer_manifest, raw)
        if result is not None:
            return result
    manager = backend(distro_info())
    arch = architecture(platform.machine())
    current = installed(manager)
    if args.action == 'update' and not current:
        raise UpdateError('Everywhere is not installed. Run the installer without update first.')
    channel = args.channel
    if channel == 'auto':
        if args.action == 'install' and not current:
            channel = choose_channel()
        elif current:
            channel = 'canary' if 'canary' in current[1] else 'stable'
        else:
            channel = 'stable'
    target, asset = select_package(read_catalog(args.catalog), channel, arch, manager)
    if current and version_key(target) <= version_key(normalize_version(current[1], manager)):
        print('Everywhere is up to date. No downgrade or reinstall performed.')
        return 0
    print('Everywhere %s (%s)' % (target, channel.capitalize()), flush=True)
    if args.dry_run:
        print('Preview complete. No packages downloaded or installed.')
        return 0
    with lock(), tempfile.TemporaryDirectory(prefix='everywhere-install-') as temporary:
        directory = Path(temporary)
        path = directory / ('download.' + {'pacman': 'tar.zst', 'apt-get': 'deb', 'dnf': 'rpm'}[manager])
        print('Downloading...', flush=True)
        fetch(asset['url'], path, asset['size'])
        verify_file(path, asset)
        if manager == 'pacman':
            for label in ('icon', 'license'):
                resource = asset.get('resources', {}).get(label)
                if not resource and args.catalog.startswith('github:'):
                    # Older fork releases keep artwork/license in the tagged source tree.
                    filename = 'img/Everywhere-icon.png' if label == 'icon' else 'LICENSE'
                    repo = args.catalog.removeprefix('github:')
                    url = 'https://raw.githubusercontent.com/%s/v%s/%s' % (repo, target, filename)
                    data = fetch(url, limit=4 * 1024 * 1024)
                    if not data:
                        raise UpdateError('Missing packaging resource: ' + label)
                    (directory / label).write_bytes(data)
                    continue
                if not resource:
                    raise UpdateError('The release is missing required packaging resources.')
                validate_asset(resource)
                if resource['size'] > 4 * 1024 * 1024:
                    raise UpdateError('Packaging resource is too large.')
                fetch(resource['url'], directory / label, resource['size'])
                verify_file(directory / label, resource)
            epoch = int(current[1].split(':', 1)[0]) if current and ':' in current[1] else 0
            path = prepare_arch(path, directory, target, current[0] if current else 'everywhere-bin',
                                Path(__file__), epoch)
        validate_package(path, manager, target, arch)
        if installed(manager) != current:
            raise UpdateError('Installation changed while downloading; please retry.')
        print('Installing...', flush=True)
        execute(package_command(manager, path), True)
        after = installed(manager)
        if not after or normalize_version(after[1], manager) != target:
            raise UpdateError('Installation completed without the expected version. Check the package manager output.')
    print('Everywhere installed successfully. Restart Everywhere to use the new version.')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (UpdateError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError, tarfile.TarError) as exc:
        print('Installation failed: ' + str(exc), file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        print('Canceled.', file=sys.stderr)
        sys.exit(130)
