#!/bin/sh
set -eu
# This small bootstrap is served by the website. The Python engine is versioned independently.
if [ "$(uname -s)" != Linux ]; then
    echo 'Unsupported system. This installer supports Linux only.' >&2
    exit 1
fi
if ! command -v python3 >/dev/null 2>&1; then
    echo 'Python 3.9+ is required. Install python3 and run this command again.' >&2
    exit 1
fi
python3 -c 'import sys; sys.exit(sys.version_info < (3, 9))' || {
    echo 'Python 3.9+ is required.' >&2
    exit 1
}
installer_tmp=$(mktemp -d)
trap 'rm -rf "$installer_tmp"' EXIT HUP INT TERM
# The initial HTTPS bootstrap is the trust entry point, just like downloading install.sh.
python3 - "$installer_tmp/installer.py" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import urllib.parse
import urllib.request


class SecureRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != 'https':
            raise ValueError('HTTPS is required.')
        return super().redirect_request(req, fp, code, msg, headers, newurl)


opener = urllib.request.build_opener(SecureRedirect())


def get(url, limit, progress=False):
    if urllib.parse.urlsplit(url).scheme != 'https':
        raise ValueError('HTTPS is required.')
    chunks, received = [], 0
    interactive = sys.stderr.isatty() and os.environ.get('TERM') != 'dumb'
    with opener.open(url, timeout=60) as response:
        while True:
            chunk = response.read(64 * 1024)
            if not chunk:
                break
            received += len(chunk)
            if received > limit:
                raise ValueError('Download exceeds its size limit.')
            chunks.append(chunk)
            if progress and interactive:
                percent = int(100 * received / limit)
                sys.stderr.write('\r\x1b[2KDownload  %3d%%  %.1f / %.1f KiB' % (
                    percent, received / 1024, limit / 1024))
                sys.stderr.flush()
    if progress:
        prefix = '\n' if interactive else 'Download  '
        suffix = '' if interactive else '%.1f / %.1f KiB\n' % (received / 1024, limit / 1024)
        sys.stderr.write(prefix + suffix)
        sys.stderr.flush()
    return b''.join(chunks)


def main():
    print('Checking installer', flush=True)
    url = os.environ.get('EVERYWHERE_INSTALLER_MANIFEST',
                         'https://raw.githubusercontent.com/Sylinko/Everywhere/main/tools/linux-installer/installer.json')
    manifest = json.loads(get(url, 65536))
    if not isinstance(manifest, dict) or manifest.get('schema') != 1 or manifest.get('protocol') != 1:
        raise ValueError('Unsupported installer protocol.')
    asset = manifest['asset']
    if (not isinstance(asset, dict) or type(asset.get('size')) is not int or
            not 0 < asset['size'] <= 4 * 1024 * 1024 or not isinstance(asset.get('sha256'), str) or
            not re.fullmatch(r'[0-9a-f]{64}', asset['sha256'])):
        raise ValueError('Invalid installer metadata.')
    data = get(asset['url'], asset['size'], progress=True)
    print('Verifying', flush=True)
    if len(data) != asset['size'] or hashlib.sha256(data).hexdigest() != asset['sha256']:
        raise ValueError('Installer checksum mismatch.')
    Path(sys.argv[1]).write_bytes(data)


try:
    main()
except (OSError, ValueError, KeyError, TypeError) as error:
    print('Installation failed: ' + str(error), file=sys.stderr)
    sys.exit(1)
except KeyboardInterrupt:
    print('Canceled.', file=sys.stderr)
    sys.exit(130)
PY
python3 "$installer_tmp/installer.py" --no-installer-update "$@"
