#!/usr/bin/env python3
"""Build static installer release files; publishing is a separate operation."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import urllib.parse

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', required=True, help='immutable HTTPS directory for this installer version')
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if urllib.parse.urlsplit(args.base_url).scheme != 'https':
    raise SystemExit('An HTTPS release URL is required.')
source = Path(__file__).with_name('installer.py')
spec = importlib.util.spec_from_file_location('installer', source)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
args.output.mkdir(parents=True, exist_ok=True)
data = source.read_bytes()
manifest = {'schema': 1, 'protocol': 1, 'version': installer.INSTALLER_VERSION, 'asset': {
    'url': args.base_url.rstrip('/') + '/installer.py', 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}}
(args.output / 'installer.json').write_text(json.dumps(manifest, indent=2) + '\n')
shutil.copyfile(source, args.output / 'installer.py')
shutil.copyfile(source.with_name('install.sh'), args.output / 'install.sh')
print('Release files generated. No files were published.')
