#!/usr/bin/env python3
"""Export the independently maintained installer and tests to an explicit checkout."""
import argparse
from pathlib import Path
import shutil

parser = argparse.ArgumentParser()
parser.add_argument('checkout', type=Path)
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
root = Path(__file__).parent
files = {
    root / 'installer.py': args.checkout / 'src/Everywhere.Linux/Update/linux-update.py',
    root / 'tests/test_installer.py': args.checkout / 'tests/linux-update/test_update.py',
    root / 'tests/test_process_flow.py': args.checkout / 'tests/linux-update/test_process_flow.py',
    root / 'tests/test_presentation.py': args.checkout / 'tests/linux-update/test_presentation.py',
}
for source, target in files.items():
    if args.check:
        if source.read_bytes() != target.read_bytes():
            raise SystemExit('Bundled installer differs; run sync-bundle.py CHECKOUT.')
    else:
        shutil.copyfile(source, target)
