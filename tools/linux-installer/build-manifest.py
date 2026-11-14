#!/usr/bin/env python3
"""Generate or verify the installer manifest committed alongside its script."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--check', action='store_true', help='fail if the committed manifest is stale')
args = parser.parse_args()
source = Path(__file__).with_name('installer.py')
spec = importlib.util.spec_from_file_location('installer', source)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
data = source.read_bytes()
manifest = {
    'schema': 1,
    'protocol': 1,
    'version': installer.INSTALLER_VERSION,
    'asset': {
        'url': 'https://raw.githubusercontent.com/Sylinko/Everywhere/main/tools/linux-installer/installer.py',
        'size': len(data),
        'sha256': hashlib.sha256(data).hexdigest(),
    },
}
output = source.with_name('installer.json')
content = json.dumps(manifest, indent=2) + '\n'
if args.check:
    if not output.exists() or output.read_text() != content:
        raise SystemExit('Installer manifest is stale; run build-manifest.py.')
else:
    output.write_text(content)
    print('Installer manifest updated. Commit it with installer.py.')
