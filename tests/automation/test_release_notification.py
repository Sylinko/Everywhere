import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))
import github_release

spec = importlib.util.spec_from_file_location("notify_aur", SCRIPTS / "notify-aur.py")
notify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notify)


class NotificationTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, {
            "GITHUB_REPOSITORY": "pasical/Everywhere", "RELEASE_TAG": "v1.2.3-canary.1",
            "EVERYWHERE_AUR_DISPATCH_TOKEN": "test-only-token",
        })
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def test_draft_does_not_dispatch(self):
        with patch.object(notify, "find_release", return_value={"draft": True}), \
                patch.object(notify.subprocess, "run") as run:
            notify.main()
            run.assert_not_called()

    def test_release_without_linux_does_not_dispatch(self):
        with patch.object(notify, "find_release", return_value={"draft": False, "assets": []}), \
                patch.object(notify.subprocess, "run") as run:
            notify.main()
            run.assert_not_called()

    def test_published_linux_release_dispatches_tag_without_overriding_source(self):
        release = {"draft": False, "assets": [{"name": "Everywhere-Linux-x64-v1.2.3-canary.1.tar.zst",
                                              "state": "uploaded", "size": 123}]}
        with patch.object(notify, "find_release", return_value=release), \
                patch.object(notify.subprocess, "run") as run:
            notify.main()
            args, kwargs = run.call_args
            self.assertIn("repos/pasical/Everywhere-AUR/actions/workflows/sync-release.yml/dispatches", args[0])
            self.assertEqual(json.loads(kwargs["input"]), {
                "ref": "main", "inputs": {"tag": "v1.2.3-canary.1", "publish_aur": True}})
            self.assertEqual(kwargs["env"]["GH_TOKEN"], "test-only-token")

    def test_draft_is_found_in_paginated_list_after_tag_endpoint_404(self):
        draft = {"tag_name": "v1.2.3", "draft": True}
        responses = [subprocess.CompletedProcess([], 1, "", "HTTP 404"),
                     subprocess.CompletedProcess([], 0, json.dumps([[{"tag_name": "other"}], [draft]]), "")]
        with patch.object(github_release.subprocess, "run", side_effect=responses):
            self.assertEqual(github_release.find_release("pasical/Everywhere", "v1.2.3"), draft)

    def test_network_error_is_not_treated_as_missing_release(self):
        response = subprocess.CompletedProcess([], 1, "", "TLS handshake timeout")
        with patch.object(github_release.subprocess, "run", return_value=response):
            with self.assertRaisesRegex(RuntimeError, "TLS"):
                github_release.find_release("pasical/Everywhere", "v1.2.3")
