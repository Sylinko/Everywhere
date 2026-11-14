#!/usr/bin/env python3
"""List every installer-owned UI, error, bootstrap and help string for review."""
import ast
from pathlib import Path
import re

ROOT = Path(__file__).parent
source = (ROOT / 'installer.py').read_text()
module = ast.parse(source)
bootstrap = (ROOT / 'install.sh').read_text()
embedded = bootstrap.split("<<'PY'\n", 1)[1].split('\nPY\n', 1)[0]


def template(node):
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        return node.value
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
        return template(node.left) + template(node.right)
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Mod):
        values = node.right.elts if isinstance(node.right, ast.Tuple) else [node.right]
        text = template(node.left)
        for value in values:
            text = re.sub(r'%[sd]', lambda match: '{' + ast.unparse(value) + '}', text, count=1)
        return text
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'str':
        return '{' + ast.unparse(node.args[0]) + '}'
    return '{' + ast.unparse(node) + '}'


def collect_errors(tree, names):
    rows = []
    for node in sorted(ast.walk(tree), key=lambda value: getattr(value, 'lineno', 0)):
        if isinstance(node, ast.Raise) and isinstance(node.exc, ast.Call):
            call = node.exc
            if isinstance(call.func, ast.Name) and call.func.id in names and call.args:
                value = template(call.args[0])
                if value not in [row[1] for row in rows]:
                    rows.append((node.lineno, value))
    return rows


ui = next(ast.literal_eval(node.value) for node in module.body
          if isinstance(node, ast.Assign) and any(isinstance(name, ast.Name) and name.id == 'COPY'
                                                  for name in node.targets))
lines = ['# Installer copy review — 0.2.0', '',
         'English text is listed exactly as owned by the installer. Braces mark runtime values.',
         'Native package-manager messages, operating-system exception details and argparse-generated',
         'usage/invalid-argument syntax are supplied by those components, not fixed installer copy.', '',
         '## Normal interface', '', '| ID | Key | English |', '| --- | --- | --- |']
for index, (key, text) in enumerate(ui.items(), 1):
    lines.append('| U%02d | %s | %s |' % (index, key, text))
lines += ['', 'Progress data: percentage; received/total bytes; average speed; estimated remaining time.',
          'Units: `B`, `KiB`, `MiB`, `GiB`, `/s`. Unknown remaining time: `--:--`.',
          'Native stages keep their normal interactive confirmations.', '',
          '## Installer errors', '', '| ID | English |', '| --- | --- |']
errors = collect_errors(module, {'UpdateError', 'DownloadError'})
for index, (_, text) in enumerate(errors, 1):
    lines.append('| E%02d | %s |' % (index, text.replace('|', '\\|')))
lines += ['', 'Every E message is displayed through `Installation failed: {error}`.', '',
          '## Bootstrap', '', '| ID | English |', '| --- | --- |']
boot_text = re.findall(r"echo '([^']+)'", bootstrap)
boot_text += [text for _, text in collect_errors(ast.parse(embedded), {'ValueError'})]
boot_text += ['Checking installer', 'Download', 'Verifying', 'Installation failed: {error}', 'Canceled.']
boot_text = list(dict.fromkeys(boot_text))
for index, text in enumerate(boot_text, 1):
    lines.append('| B%02d | %s |' % (index, text))
lines += ['', '## CLI help', '', '| ID | Option | English |', '| --- | --- | --- |']
help_rows = []
for node in ast.walk(module):
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == 'add_argument':
        help_text = next((value.value for value in node.keywords if value.arg == 'help'), None)
        if isinstance(help_text, ast.Constant) and isinstance(help_text.value, str):
            help_rows.append((node.args[0].value, help_text.value))
for index, (option, text) in enumerate(help_rows, 1):
    lines.append('| H%02d | %s | %s |' % (index, option, text))
lines += ['', 'Program: `Everywhere installer`. Actions: `install`, `update`.',
          'Channel values: `stable`, `canary`, `auto`. The compatibility `--apply` flag is hidden.', '',
          '## Interface sample', '', '```text', ' Everywhere', '',
          '╭─ Release channel ─────────────────────╮',
          '│❯ Stable                              │',
          '│  Canary                              │',
          '╰──────────────────────────────────────╯',
          '↑/↓ Navigate · Enter Select · Esc Cancel', '',
          'Checking releases', '0.9.0-canary.20261110.31 · Canary',
          'Download  ━━━━━━━━━─────   60%  39.4 / 65.7 MiB  4.1 MiB/s  00:06',
          'Verifying', 'Preparing package', 'Installing', 'Updated to 0.9.0-canary.20261110.31',
          'Restart Everywhere.', '```', '',
          'The sample version and progress values illustrate layout; they are not live download evidence.', '']
(ROOT / 'COPY-REVIEW.md').write_text('\n'.join(lines))
print('Copy inventory: %d UI, %d installer errors, %d bootstrap, %d CLI help entries.' % (
    len(ui), len(errors), len(boot_text), len(help_rows)))
