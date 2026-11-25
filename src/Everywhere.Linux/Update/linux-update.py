#!/usr/bin/env python3
"""Independent Everywhere Linux installer (Python 3.9+, standard library)."""
import argparse
import contextlib
try:
    import fcntl
    import termios
    import tty
except ImportError:
    fcntl = None
    termios = None
    tty = None
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


COPY = {
    'title': 'Everywhere',
    'tagline': 'Context-aware Desktop AI Assistant',
    'channel': 'Release channel',
    'channel_compact': 'Channel',
    'stable': 'Stable',
    'stable_desc': 'Recommended · Official release',
    'canary': 'Canary',
    'canary_desc': 'Preview · Experimental build',
    'keys': '↑/↓ Navigate · Enter Select · Esc Cancel',
    'keys_compact': '↑↓  Enter  Esc',
    'system_label': 'System',
    'desktop_label': 'Desktop',
    'checking_installer': 'Checking installer',
    'bundled_installer': 'Using bundled installer (check unavailable).',
    'using_installer': 'Installer {version}',
    'checking_release': 'Checking releases',
    'target': '{version} · {channel}',
    'download': 'Download',
    'retry': 'Retrying download ({attempt}/3)',
    'verify': 'Verifying',
    'prepare': 'Preparing package',
    'install': 'Installing',
    'installed': 'Installed {version}',
    'updated': 'Updated to {version}',
    'open': 'Open Everywhere from your app menu, or run: Everywhere',
    'launch_update': 'Run anytime in terminal: ew update',
    'hyprland_tip': 'Tip: Install grim and wl-clipboard for screenshot & selection support.',
    'restart': 'Restart Everywhere.',
    'current': 'Already up to date.',
    'older': 'No newer version in this channel.',
    'preview': 'Preview complete.',
    'failed': 'Installation failed: {error}',
    'canceled': 'Canceled.',
}

BANNER_MASCOT = (
    '  ╭─────────╮  ',
    '  │  █   █  │  ',
    '  │         │  ',
    '  ╰─────────╯  ',
)

BANNER_WORD = (
    '█▀▀▀ █   █ █▀▀▀ █▀▀▄ █   █ █   █ █  █ █▀▀▀ █▀▀▄ █▀▀▀',
    '█▀▀  ▀▄ ▄▀ █▀▀  █▀▀▄  ▀█▀  █ █ █ █▀▀█ █▀▀  █▀▀▄ █▀▀ ',
    '▀▀▀▀   ▀   ▀▀▀▀ ▀  ▀   ▀    ▀ ▀  ▀  ▀ ▀▀▀▀ ▀  ▀ ▀▀▀▀',
)


def session_summary(environ=None):
    env = os.environ if environ is None else environ
    session_type = env.get('XDG_SESSION_TYPE', '').lower()
    display = env.get('DISPLAY', '')
    wayland_display = env.get('WAYLAND_DISPLAY', '')
    desktops = [item.strip().lower() for item in env.get('XDG_CURRENT_DESKTOP', '').split(':') if item.strip()]
    hyprland = env.get('HYPRLAND_INSTANCE_SIGNATURE', '')
    if session_type == 'x11' and display:
        return 'X11 · Standard integration'
    if wayland_display:
        if 'kde' in desktops or 'plasma' in desktops:
            return 'KDE Plasma (Wayland) · Window & shortcut mode'
        if 'gnome' in desktops:
            return 'GNOME (Wayland) · Window & shortcut mode'
        if 'niri' in desktops:
            return 'Niri (Wayland) · Window mode'
        if 'hyprland' in desktops or hyprland:
            return 'Hyprland (Wayland) · Full integration'
    if hyprland:
        return 'Hyprland (Wayland) · Full integration'
    if display:
        return 'X11 · Standard integration'
    return None


class TerminalUI:
    """Use the terminal palette and keep output readable when redirected."""

    def __init__(self, stream=None):
        self.stream = stream if stream is not None else sys.stdout
        self.is_interactive = self.stream.isatty() and os.environ.get('TERM') != 'dumb'
        self.has_color = self.is_interactive and 'NO_COLOR' not in os.environ
        colorterm = os.environ.get('COLORTERM', '').lower()
        term = os.environ.get('TERM', '').lower()
        if not self.has_color:
            self.color_mode = 'none'
        elif colorterm in ('truecolor', '24bit'):
            self.color_mode = 'truecolor'
        elif '256color' in term:
            self.color_mode = '256'
        else:
            self.color_mode = '16'

    def style(self, text, role='accent'):
        if not self.has_color:
            return text
        codes = {'accent': '1;36', 'selected': '1;94', 'muted': '90', 'success': '1;32', 'error': '1;31'}
        return '\x1b[%sm%s\x1b[0m' % (codes[role], text)

    def _paint_banner_line(self, line, total_width, mascot_width):
        if not self.has_color:
            return line
        stops_256 = (39, 75, 111, 141, 171, 205)
        parts = []
        current_code = None
        for col, char in enumerate(line):
            if char == ' ':
                code = None
            elif col < mascot_width and char == '█':
                code = '1;97'
            else:
                ratio = col / max(1, total_width - 1)
                if self.color_mode == 'truecolor':
                    if ratio < 0.5:
                        local = ratio * 2
                        rgb = (int(56 + 83 * local), int(189 - 97 * local), int(248 - 2 * local))
                    else:
                        local = (ratio - 0.5) * 2
                        rgb = (int(139 + 97 * local), int(92 - 20 * local), int(246 - 93 * local))
                    code = '1;38;2;%d;%d;%d' % rgb
                elif self.color_mode == '256':
                    code = '1;38;5;%d' % stops_256[min(len(stops_256) - 1, int(ratio * len(stops_256)))]
                else:
                    code = '1;36' if ratio < 0.5 else '1;94'
            if code != current_code:
                if current_code is not None:
                    parts.append('\x1b[0m')
                if code is not None:
                    parts.append('\x1b[%sm' % code)
                current_code = code
            parts.append(char)
        if current_code is not None:
            parts.append('\x1b[0m')
        return ''.join(parts)

    def write(self, text):
        self.stream.write(text + '\n')
        self.stream.flush()

    def header(self, width=None):
        if width is None:
            width = shutil.get_terminal_size((80, 24)).columns
        if width >= 68:
            lines = [
                BANNER_MASCOT[0] + BANNER_WORD[0],
                BANNER_MASCOT[1] + BANNER_WORD[1],
                BANNER_MASCOT[2] + BANNER_WORD[2],
            ]
            total_width = max(len(item) for item in lines)
            mascot_width = len(BANNER_MASCOT[0])
            rendered = [self._paint_banner_line(item, total_width, mascot_width) for item in lines]
            rendered.append(
                self._paint_banner_line(BANNER_MASCOT[3], total_width, mascot_width)
                + self.style(COPY['tagline'], 'muted')
            )
            self.write('\n' + '\n'.join(rendered) + '\n')
            return
        compact = ' ' + self.style(COPY['title'])
        if width >= 50:
            compact += '  ' + self.style(COPY['tagline'], 'muted')
        self.write('\n' + compact + '\n')

    def info_row(self, label_key, value, width=None):
        if width is None:
            width = shutil.get_terminal_size((80, 24)).columns
        label = COPY[label_key].ljust(9)
        if len(label) + len(value) + 3 > width:
            available = max(4, width - len(label) - 3)
            value = value[:available - 1] + '…' if available > 1 else value[:available]
        self.write('  ' + self.style(label, 'muted') + ' ' + value)

    def environment(self, info, machine, manager, width=None):
        pretty = info.get('PRETTY_NAME') or info.get('NAME') or info.get('ID') or 'Linux'
        self.info_row('system_label', '%s (%s) · %s' % (pretty, machine, manager), width=width)
        desktop = session_summary()
        if desktop:
            self.info_row('desktop_label', desktop, width=width)
        self.write('')

    def status(self, key, role='muted', prefix=None, **values):
        text = COPY[key].format(**values)
        if prefix and self.is_interactive:
            text = '%s %s' % (prefix, text)
        self.write(self.style(text, role))

    def choice(self, selected, width):
        """Render a bounded card; only the two options are interactive."""
        inner = max(12, min(50, width - 4))
        title = COPY['channel'] if inner >= 20 else COPY['channel_compact']
        top = '╭─ ' + title + ' ' + '─' * max(0, inner - len(title) - 3) + '╮'
        lines = [self.style(top)]
        for index, key in enumerate(('stable', 'canary')):
            marker = '❯ ' if index == selected else '  '
            name = COPY[key]
            label = (' %s%-8s %s' % (marker, name, COPY[key + '_desc'])) if inner >= 46 else (marker + name)
            content = label[:inner].ljust(inner)
            if index == selected:
                content = self.style(content, 'selected')
            lines.append(self.style('│') + content + self.style('│'))
        lines.append(self.style('╰' + '─' * inner + '╯'))
        hint = COPY['keys'] if width >= 48 else COPY['keys_compact']
        lines.append(self.style(hint, 'muted'))
        return '\r\n'.join(lines)


def byte_size(value):
    for suffix in ('B', 'KiB', 'MiB', 'GiB'):
        if value < 1024 or suffix == 'GiB':
            return ('%.1f %s' % (value, suffix)) if suffix != 'B' else '%d B' % value
        value /= 1024


class DownloadProgress:
    """Render actual received bytes, including files written by the .NET helper."""

    def __init__(self, total, stream=None):
        self.total = total
        self.ui = TerminalUI(sys.stderr if stream is None else stream)
        self.started = time.monotonic()
        self.last_draw = 0.0
        self.received = 0

    def __enter__(self):
        self.update(0, force=True)
        return self

    def update(self, received, force=False):
        self.received = received
        now = time.monotonic()
        if not self.ui.is_interactive or not force and now - self.last_draw < 0.1:
            return
        self.last_draw = now
        width = shutil.get_terminal_size((80, 24)).columns
        ratio = min(1.0, received / self.total) if self.total else 0
        rate = received / max(0.01, now - self.started)
        size = '%s / %s' % (byte_size(received), byte_size(self.total))
        percent = '%3d%%' % (100 * ratio)
        if width < 56:
            size = byte_size(received)
        if width >= 72:
            remaining = int(max(0, self.total - received) / rate) if rate else 0
            estimate = '%02d:%02d' % (remaining // 60, remaining % 60) if rate else '--:--'
            size += '  %s/s  %s' % (byte_size(rate), estimate)
        bar_width = max(4, min(20, width - len(size) - 22))
        filled = int(bar_width * ratio)
        bar = '━' * filled + '─' * (bar_width - filled)
        line = '%s  %s  %s  %s' % (COPY['download'], self.ui.style(bar), percent, size)
        if width < 32:
            line = '%s  %s' % (percent.strip(), size)
        self.ui.stream.write('\r\x1b[2K' + line)
        self.ui.stream.flush()

    def __exit__(self, exception_type, exception, traceback):
        if self.ui.is_interactive:
            self.update(self.received, force=True)
            self.ui.stream.write('\n')
            self.ui.stream.flush()
        elif exception_type is None:
            self.ui.write('%s  %s / %s' % (COPY['download'], byte_size(self.received), byte_size(self.total)))


UI = TerminalUI()


def receive_with_transport(command, output, limit, show_progress):
    if not show_progress:
        try:
            return subprocess.run(command, timeout=200, stdout=subprocess.DEVNULL,
                                  stderr=subprocess.DEVNULL).returncode
        except subprocess.TimeoutExpired as exception:
            raise DownloadError('Download timed out.') from exception
    with DownloadProgress(limit) as progress:
        process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 200
        try:
            while process.poll() is None:
                received = output.stat().st_size if output.exists() else 0
                if received > limit:
                    raise UpdateError('Download exceeds its size limit.')
                progress.update(received)
                if time.monotonic() > deadline:
                    raise DownloadError('Download timed out.')
                time.sleep(0.1)
            received = output.stat().st_size if output.exists() else 0
            if received > limit:
                raise UpdateError('Download exceeds its size limit.')
            progress.update(received, force=True)
            if process.returncode != 0:
                raise DownloadError('Download failed using the application network settings.')
            return process.returncode
        except BaseException:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            raise


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
                output.unlink(missing_ok=True)
                try:
                    result = receive_with_transport(command_line + [url, str(output), str(limit)],
                                                    output, limit, destination is not None)
                    if result == 0:
                        if not output.is_file() or output.stat().st_size > limit:
                            raise UpdateError('Application transport returned an invalid response.')
                        return b'' if destination else output.read_bytes()
                except DownloadError:
                    if attempt == 2:
                        raise
                if attempt < 2:
                    if destination:
                        UI.status('retry', attempt=attempt + 2)
                    time.sleep(attempt + 1)
            raise DownloadError('Download failed using the application network settings.')
    opener = urllib.request.build_opener(HTTPSRedirect())
    netloc = urllib.parse.urlsplit(url).netloc
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={
                'User-Agent': 'Everywhere-Linux-Installer', 'Accept': 'application/vnd.github+json'})
            if netloc == 'api.github.com':
                token = (os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or '').strip()
                if token and not any(ch.isspace() for ch in token):
                    req.add_unredirected_header('Authorization', 'Bearer ' + token)
            with opener.open(req, timeout=30) as response:
                output_context = open(destination, 'wb') if destination else contextlib.nullcontext()
                progress_context = DownloadProgress(limit) if destination else contextlib.nullcontext()
                with output_context as output, progress_context as progress:
                    chunks, count = [], 0
                    while True:
                        chunk = response.read(64 * 1024)
                        if not chunk:
                            break
                        count += len(chunk)
                        if count > limit:
                            raise UpdateError('Response exceeds expected size limit.')
                        if output:
                            output.write(chunk)
                            progress.update(count)
                        else:
                            chunks.append(chunk)
                    return b''.join(chunks)
        except urllib.error.HTTPError as exc:
            if (exc.code in (403, 429) and netloc == 'api.github.com' and
                    exc.headers and exc.headers.get('X-RateLimit-Remaining') == '0'):
                raise DownloadError('GitHub API rate limit exceeded. Use the default catalog or set GITHUB_TOKEN.') from exc
            if exc.code not in (429, 500, 502, 503, 504) or attempt == 2:
                raise DownloadError('Download HTTP %s: %s' % (exc.code, url)) from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            if attempt == 2:
                raise DownloadError('Download failed: ' + str(exc)) from exc
        if destination:
            UI.status('retry', attempt=attempt + 2)
        time.sleep(attempt + 1)


INSTALLER_VERSION = '0.3.0'
INSTALLER_URL = 'https://download.sylinko.com/everywhere/linux/installer.json'
CATALOG_URL = 'https://download.sylinko.com/everywhere/linux/releases.json'


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
        UI.status('bundled_installer')
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
            try:
                fetch(asset['url'], download, asset['size'])
            except DownloadError:
                UI.status('bundled_installer')
                return None
            verify_file(download, asset)
            os.replace(download, script)
    UI.status('using_installer', version=version)
    return subprocess.run([sys.executable, str(script), '--no-installer-update', *arguments], check=False).returncode


def choose_channel():
    """Read the controlling terminal, including when stdin contains a curl script."""
    try:
        terminal = open('/dev/tty', 'r+b', buffering=0)
    except OSError as exc:
        raise UpdateError('Choose --channel stable or --channel canary in a terminal.') from exc
    with terminal:
        fd = terminal.fileno()
        previous = termios.tcgetattr(fd)
        selected = 0
        # The menu owns /dev/tty even if stdout is being logged separately.
        ui = TerminalUI()
        is_interactive = os.environ.get('TERM') != 'dumb'
        ui.has_color = is_interactive and 'NO_COLOR' not in os.environ
        try:
            tty.setraw(fd)
            if is_interactive:
                terminal.write(b'\x1b[?25l')
            while True:
                width = shutil.get_terminal_size((80, 24)).columns
                terminal.write(ui.choice(selected, width).encode())
                key = terminal.read(1)
                if key in (b'\r', b'\n'):
                    return COPY['stable' if selected == 0 else 'canary'].lower()
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
                if is_interactive:
                    terminal.write(b'\x1b[4A\r')
                else:
                    terminal.write(b'\r\n')
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, previous)
            if is_interactive:
                terminal.write(b'\x1b[?25h')
            terminal.write(b'\r\n')


def backend(info):
    """Keep system support in the independently released installer."""
    raw_distro = info.get('ID', '').strip()
    raw_version = info.get('VERSION_ID', '').strip()
    distro = raw_distro.lower()
    id_like = info.get('ID_LIKE', '').lower().split()
    major_match = re.match(r'^(\d+)', raw_version)
    major = int(major_match.group(1)) if major_match else None
    if Path('/run/ostree-booted').exists():
        raise UpdateError('This immutable system is not supported.')
    if (not distro or distro in ('nixos', 'guix', 'alpine', 'gentoo', 'void', 'slackware') or
            'suse' in distro or any('suse' in item for item in id_like)):
        raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
    if distro in ('arch', 'endeavouros', 'manjaro', 'cachyos', 'garuda', 'artix', 'archcraft',
                  'arcolinux', 'rebornos') or 'arch' in id_like:
        manager = 'pacman'
    elif distro == 'ubuntu':
        if major is None or major < 24:
            raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
        manager = 'apt-get'
    elif distro == 'debian':
        if major is not None and major < 13:
            raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
        manager = 'apt-get'
    elif (distro in ('linuxmint', 'pop', 'zorin', 'elementary', 'kali', 'neon', 'tuxedo') or
          'ubuntu' in id_like or 'debian' in id_like):
        if ((distro == 'linuxmint' and major is not None and major < 22) or
                (distro == 'pop' and major is not None and major < 24) or
                (distro == 'zorin' and major is not None and major < 18)):
            raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
        manager = 'apt-get'
    elif distro == 'fedora':
        if major is not None and major < 40:
            raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
        manager = 'dnf'
    elif (distro in ('nobara', 'ultramarine', 'rhel', 'centos', 'rocky', 'almalinux') or
          'fedora' in id_like):
        if distro in ('rhel', 'centos', 'rocky', 'almalinux') and (major is None or major < 10):
            raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
        manager = 'dnf'
    else:
        raise UpdateError('Unsupported system: %s %s.' % (raw_distro, raw_version))
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


def apply_mirror(url, mirror, target, arch, extension):
    if mirror is None:
        return url
    value = mirror.strip()
    if value == 'official':
        return url
    filename = 'Everywhere-Linux-%s-v%s.%s' % (arch, target, extension)
    github_url = 'https://github.com/Sylinko/Everywhere/releases/download/v%s/%s' % (target, filename)
    if value == 'github':
        return github_url
    parts = urllib.parse.urlsplit(value)
    if parts.scheme != 'https' or not parts.netloc or parts.username or parts.password or any(ch.isspace() for ch in value):
        raise UpdateError('Mirror must be official, github, or an HTTPS URL.')
    if any(token in value for token in ('{url}', '{file}', '{tag}', '{version}')):
        try:
            resolved = value.format(url=github_url, file=filename, tag='v' + target, version=target)
        except (KeyError, ValueError, IndexError) as exc:
            raise UpdateError('Mirror must be official, github, or an HTTPS URL.') from exc
    else:
        resolved = value.rstrip('/') + '/' + github_url
    resolved_parts = urllib.parse.urlsplit(resolved)
    if resolved_parts.scheme != 'https' or not resolved_parts.netloc:
        raise UpdateError('Mirror must be official, github, or an HTTPS URL.')
    return resolved


def apply_raw_mirror(raw_url, mirror):
    if mirror is None:
        return raw_url
    value = mirror.strip()
    if value in ('official', 'github') or any(token in value for token in ('{url}', '{file}', '{tag}', '{version}')):
        return raw_url
    return value.rstrip('/') + '/' + raw_url


def prepare_arch(archive, directory, target, name, bundled_script, epoch, resource_fallback=None):
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
    if not (directory / 'icon').is_file() and (payload / 'Everywhere-icon.png').is_file():
        shutil.copyfile(payload / 'Everywhere-icon.png', directory / 'icon')
    if not (directory / 'license').is_file() and (payload / 'LICENSE').is_file():
        shutil.copyfile(payload / 'LICENSE', directory / 'license')
    if resource_fallback is not None:
        resource_fallback(directory)
    pkgver = target.replace('-canary.', 'canary.')
    architecture_name = {'x64': 'x86_64', 'arm64': 'aarch64'}[architecture(platform.machine())]
    recipe = """pkgname=%s
pkgver=%s
pkgrel=1
epoch=%s
pkgdesc='Everywhere desktop assistant'
arch=('%s')
license=('AGPL-3.0-only')
provides=('everywhere')
conflicts=('everywhere')
depends=('python' 'glibc' 'gcc-libs' 'zlib' 'openssl' 'icu' 'fontconfig'
         'libx11' 'libice' 'libsm' 'libxext' 'libxcb' 'libxrandr' 'libxrender'
         'libxi' 'libxcursor' 'wayland' 'libxkbcommon' 'glib2' 'at-spi2-core' 'libsecret' 'xdg-utils' 'desktop-file-utils')
optdepends=('grim: Hyprland screenshot capture'
            'wl-clipboard: Wayland text-selection monitoring'
            'libnotify: desktop notifications')
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
    # Keep generated packages inside this transaction despite user makepkg settings.
    execute(['makepkg', f'PKGDEST={directory}', '--syncdeps', '--cleanbuild', '--force'], False, cwd=directory)
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
    parser.add_argument('--mirror', help='select official, github, or an HTTPS GitHub mirror prefix')
    parser.add_argument('--proxy', help='override application proxy for this invocation')
    parser.add_argument('--installer-version', action='version', version=INSTALLER_VERSION)
    raw = list(sys.argv[1:] if arguments is None else arguments)
    args = parser.parse_args(raw)
    if platform.system() != 'Linux':
        raise UpdateError('This installer supports Linux only.')
    if args.mirror is not None:
        apply_mirror('https://example.invalid', args.mirror, '0.0.0', 'x64', 'deb')
    if args.proxy:
        if not os.environ.get('EVERYWHERE_UPDATE_TRANSPORT'):
            raise UpdateError('Use HTTPS_PROXY for standalone installation; --proxy requires ew update.')
        os.environ['EVERYWHERE_UPDATE_PROXY'] = args.proxy
    if not args.no_installer_update:
        UI.status('checking_installer')
        result = refresh_installer(args.installer_manifest, raw)
        if result is not None:
            return result
    UI.header()
    info = distro_info()
    manager = backend(info)
    arch = architecture(platform.machine())
    UI.environment(info, platform.machine(), manager)
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
    UI.status('checking_release')
    target, asset = select_package(read_catalog(args.catalog), channel, arch, manager)
    if current and version_key(target) <= version_key(normalize_version(current[1], manager)):
        current_version = normalize_version(current[1], manager)
        UI.status('current' if version_key(target) == version_key(current_version) else 'older', role='success', prefix='✓')
        return 0
    UI.status('target', role='accent', prefix='◆', version=target, channel=channel.capitalize())
    if args.dry_run:
        UI.status('preview', prefix='✓')
        return 0
    with lock(), tempfile.TemporaryDirectory(prefix='everywhere-install-') as temporary:
        directory = Path(temporary)
        extension = {'pacman': 'tar.zst', 'apt-get': 'deb', 'dnf': 'rpm'}[manager]
        path = directory / ('download.' + extension)
        download_url = apply_mirror(asset['url'], args.mirror, target, arch, extension)
        fetch(download_url, path, asset['size'])
        UI.status('verify', prefix='•')
        verify_file(path, asset)
        if manager == 'pacman':
            for label in ('icon', 'license'):
                resource = asset.get('resources', {}).get(label)
                if not resource:
                    continue
                validate_asset(resource)
                if resource['size'] > 4 * 1024 * 1024:
                    raise UpdateError('Packaging resource is too large.')
                fetch(resource['url'], directory / label, resource['size'])
                verify_file(directory / label, resource)

            def fetch_missing_resources(pkg_dir):
                for label in ('icon', 'license'):
                    if (pkg_dir / label).is_file():
                        continue
                    if args.catalog.startswith('github:') or (args.mirror and args.mirror.strip() != 'official'):
                        # Older releases keep artwork/license in the tagged source tree.
                        filename = 'img/Everywhere-icon.png' if label == 'icon' else 'LICENSE'
                        repo = args.catalog.removeprefix('github:') if args.catalog.startswith('github:') else 'Sylinko/Everywhere'
                        url = apply_raw_mirror('https://raw.githubusercontent.com/%s/v%s/%s' % (repo, target, filename), args.mirror)
                    elif args.catalog == CATALOG_URL:
                        res_name = 'Everywhere-icon.png' if label == 'icon' else 'LICENSE'
                        url = 'https://download.sylinko.com/everywhere/linux/resources/%s?tag=v%s' % (res_name, target)
                    else:
                        raise UpdateError('The release is missing required packaging resources.')
                    data = fetch(url, limit=4 * 1024 * 1024)
                    if not data:
                        raise UpdateError('Missing packaging resource: ' + label)
                    (pkg_dir / label).write_bytes(data)

            UI.status('prepare', prefix='•')
            epoch = int(current[1].split(':', 1)[0]) if current and ':' in current[1] else 0
            path = prepare_arch(path, directory, target, current[0] if current else 'everywhere-bin',
                                Path(__file__), epoch, resource_fallback=fetch_missing_resources)
        validate_package(path, manager, target, arch)
        if installed(manager) != current:
            raise UpdateError('Installation changed while downloading; please retry.')
        UI.status('install', prefix='•')
        execute(package_command(manager, path), True)
        after = installed(manager)
        if not after or normalize_version(after[1], manager) != target:
            raise UpdateError('Installation completed without the expected version. Check the package manager output.')
    UI.status('updated' if current else 'installed', role='success', prefix='✓', version=target)
    if current:
        UI.status('restart')
    else:
        UI.status('open')
        UI.status('launch_update')
    if os.environ.get('HYPRLAND_INSTANCE_SIGNATURE') and (not shutil.which('grim') or not shutil.which('wl-paste')):
        UI.status('hyprland_tip', role='accent')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (UpdateError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError, tarfile.TarError) as exc:
        TerminalUI(sys.stderr).status('failed', role='error', error=str(exc))
        sys.exit(1)
    except KeyboardInterrupt:
        TerminalUI(sys.stderr).status('canceled')
        sys.exit(130)
