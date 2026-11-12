import importlib.util
import io
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'installer.py'
if not SOURCE.exists():
    SOURCE = ROOT.parent / 'src/Everywhere.Linux/Update/linux-update.py'
spec = importlib.util.spec_from_file_location('installer_ui', SOURCE)
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)
ANSI = re.compile(r'\x1b\[[0-9;?]*[A-Za-z]')


class TtyOutput(io.StringIO):
    def isatty(self):
        return True


class PresentationTests(unittest.TestCase):
    def test_choice_uses_terminal_palette_and_no_installation_method(self):
        with patch.dict(os.environ, {'TERM': 'xterm-256color'}, clear=True):
            ui = u.TerminalUI(TtyOutput())
            frame = ui.choice(1, 80)
            self.assertIn('❯ Canary', ANSI.sub('', frame))
            self.assertIn('\x1b[1;94m', frame)
            self.assertIn('Release channel', frame)
            self.assertNotIn('pacman', frame)
            self.assertNotIn('sudo', frame)
            self.assertNotIn('Ubuntu', frame)
            for width in (20, 40, 80):
                for line in ANSI.sub('', ui.choice(0, width)).splitlines():
                    self.assertLessEqual(len(line), width)

    def test_no_color_and_dumb_terminal(self):
        with patch.dict(os.environ, {'NO_COLOR': '', 'TERM': 'xterm'}, clear=True):
            self.assertNotIn('\x1b', u.TerminalUI(TtyOutput()).choice(0, 80))
        with patch.dict(os.environ, {'TERM': 'dumb'}, clear=True):
            ui = u.TerminalUI(TtyOutput())
            self.assertFalse(ui.is_interactive)
            self.assertNotIn('\x1b', ui.choice(0, 80))

    def test_progress_is_bounded_at_narrow_terminal_widths(self):
        for width in (20, 40, 80):
            stream = TtyOutput()
            with patch.dict(os.environ, {'TERM': 'xterm'}, clear=True), \
                    patch.object(u.shutil, 'get_terminal_size', return_value=os.terminal_size((width, 24))):
                with u.DownloadProgress(1024 * 1024, stream) as progress:
                    progress.update(512 * 1024, force=True)
                output = ANSI.sub('', stream.getvalue())
                self.assertIn('50%', output)
                for line in output.replace('\r', '\n').splitlines():
                    self.assertLessEqual(len(line), width)

    def test_redirected_progress_has_no_escape_sequences_or_per_chunk_lines(self):
        stream = io.StringIO()
        with u.DownloadProgress(100, stream) as progress:
            for count in (10, 40, 80, 100):
                progress.update(count, force=True)
        self.assertNotIn('\x1b', stream.getvalue())
        self.assertEqual(len(stream.getvalue().splitlines()), 1)
        self.assertIn('100 B / 100 B', stream.getvalue())

    def test_https_download_reports_received_chunks(self):
        payload = b'x' * (256 * 1024)
        observations = []
        class ObservedProgress(u.DownloadProgress):
            def update(self, count, force=False):
                observations.append(count)
                self.received = count
        with tempfile.TemporaryDirectory() as temporary, \
                patch.dict(os.environ, {'EVERYWHERE_UPDATE_TRANSPORT': ''}), \
                patch.object(u.urllib.request, 'build_opener') as opener, \
                patch.object(u, 'DownloadProgress', ObservedProgress):
            opener.return_value.open.return_value = io.BytesIO(payload)
            destination = Path(temporary) / 'payload'
            u.fetch('https://test.invalid/application', destination, len(payload))
            self.assertEqual(destination.read_bytes(), payload)
        self.assertEqual(observations, [0, 65536, 131072, 196608, 262144])

    def test_application_transport_reports_real_file_growth(self):
        observations = []
        class ObservedProgress(u.DownloadProgress):
            def update(self, count, force=False):
                observations.append(count)
                self.received = count
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            helper = root / 'helper.py'
            helper.write_text('import sys,time\nwith open(sys.argv[2],"wb") as f:\n'
                              ' for i in range(4):\n  f.write(b"x"*1024);f.flush();time.sleep(.15)\n')
            destination = root / 'payload'
            with patch.object(u, 'DownloadProgress', ObservedProgress):
                result = u.receive_with_transport([sys.executable, str(helper), 'https://test.invalid',
                                                    str(destination), '4096'], destination, 4096, True)
            self.assertEqual(result, 0)
            self.assertEqual(destination.stat().st_size, 4096)
        self.assertGreaterEqual(len(set(value for value in observations if 0 < value < 4096)), 2)
        self.assertEqual(observations[-1], 4096)

    def test_canceling_transport_reaps_the_downloader(self):
        processes = []
        real_popen = subprocess.Popen
        def start(*args, **kwargs):
            process = real_popen(*args, **kwargs)
            processes.append(process)
            return process
        class CancelProgress(u.DownloadProgress):
            def update(self, count, force=False):
                self.received = count
                if count:
                    raise KeyboardInterrupt
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            helper = root / 'helper.py'
            helper.write_text('import sys,time\nopen(sys.argv[2],"wb").write(b"x")\ntime.sleep(30)\n')
            with patch.object(u.subprocess, 'Popen', side_effect=start), \
                    patch.object(u, 'DownloadProgress', CancelProgress):
                with self.assertRaises(KeyboardInterrupt):
                    u.receive_with_transport([sys.executable, str(helper), 'https://test.invalid',
                                              str(root / 'payload'), '100'], root / 'payload', 100, True)
        self.assertEqual(len(processes), 1)
        self.assertIsNotNone(processes[0].poll())


if __name__ == '__main__':
    unittest.main()
