import importlib.util
import json
import os
import tempfile
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
            "GITHUB_REPOSITORY": "Sylinko/Everywhere", "RELEASE_TAG": "v1.2.3-canary.1",
            "AUR_MIRROR_REPOSITORY": "maintainer/linux-release-mirror",
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

    def test_unusable_linux_assets_do_not_dispatch(self):
        required = "Everywhere-Linux-x64-v1.2.3-canary.1.tar.zst"
        for asset in (
            {"name": "unrelated.tar.zst", "state": "uploaded", "size": 123},
            {"name": required, "state": "uploaded", "size": 0},
            {"name": required, "state": "open", "size": 123},
        ):
            with self.subTest(asset=asset), \
                    patch.object(notify, "find_release", return_value={"draft": False, "assets": [asset]}), \
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
            self.assertIn("repos/maintainer/linux-release-mirror/actions/workflows/sync-release.yml/dispatches", args[0])
            self.assertEqual(json.loads(kwargs["input"]), {
                "ref": "main", "inputs": {"tag": "v1.2.3-canary.1", "publish_aur": True}})
            self.assertEqual(kwargs["env"]["GH_TOKEN"], "test-only-token")
            self.assertEqual(kwargs["timeout"], 120)

    def test_dispatch_timeout_is_not_reported_as_success(self):
        release = {"draft": False, "assets": [{"name": "Everywhere-Linux-x64-v1.2.3-canary.1.tar.zst",
                                              "state": "uploaded", "size": 123}]}
        with patch.object(notify, "find_release", return_value=release), \
                patch.object(notify.subprocess, "run", side_effect=subprocess.TimeoutExpired("gh api", 120)):
            with self.assertRaises(subprocess.TimeoutExpired):
                notify.main()

    def test_release_lookup_timeout_is_not_treated_as_missing_release(self):
        with patch.object(github_release.subprocess, "run", side_effect=subprocess.TimeoutExpired("gh api", 120)):
            with self.assertRaises(subprocess.TimeoutExpired):
                github_release.find_release("Sylinko/Everywhere", "v1.2.3")

    def test_paginated_release_lookup_timeout_is_not_treated_as_missing_release(self):
        responses = [subprocess.CompletedProcess([], 1, "", "HTTP 404"),
                     subprocess.TimeoutExpired("gh api", 120)]
        with patch.object(github_release.subprocess, "run", side_effect=responses):
            with self.assertRaises(subprocess.TimeoutExpired):
                github_release.find_release("Sylinko/Everywhere", "v1.2.3")

    def test_draft_is_found_in_paginated_list_after_tag_endpoint_404(self):
        draft = {"tag_name": "v1.2.3", "draft": True}
        responses = [subprocess.CompletedProcess([], 1, "", "HTTP 404"),
                     subprocess.CompletedProcess([], 0, json.dumps([[{"tag_name": "other"}], [draft]]), "")]
        with patch.object(github_release.subprocess, "run", side_effect=responses) as run:
            self.assertEqual(github_release.find_release("Sylinko/Everywhere", "v1.2.3"), draft)
            self.assertTrue(all(call.kwargs["timeout"] == 120 for call in run.call_args_list))

    def test_network_error_is_not_treated_as_missing_release(self):
        response = subprocess.CompletedProcess([], 1, "", "TLS handshake timeout")
        with patch.object(github_release.subprocess, "run", return_value=response):
            with self.assertRaisesRegex(RuntimeError, "TLS"):
                github_release.find_release("Sylinko/Everywhere", "v1.2.3")

    def test_unconfigured_upstream_skips_notification_without_reading_release(self):
        with patch.dict(os.environ, {"AUR_MIRROR_REPOSITORY": ""}), \
                patch.object(notify, "find_release") as read, patch.object(notify.subprocess, "run") as run:
            notify.main()
            read.assert_not_called()
            run.assert_not_called()

    def test_configured_destination_requires_dispatch_token(self):
        release = {"draft": False, "assets": [{"name": "Everywhere-Linux-x64-v1.2.3-canary.1.tar.zst",
                                              "state": "uploaded", "size": 123}]}
        with patch.dict(os.environ, {"EVERYWHERE_AUR_DISPATCH_TOKEN": ""}), \
                patch.object(notify, "find_release", return_value=release):
            with self.assertRaisesRegex(ValueError, "Actions:write"):
                notify.main()


class ReleasePublicationTests(unittest.TestCase):
    repository = "Sylinko/Everywhere"

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.assets = Path(temp.name)

    def make_packages(self, tag, *, linux_only=False):
        names = [f"Everywhere-Linux-x64-{tag}.{ext}" for ext in ("deb", "rpm", "tar.zst")]
        if not linux_only:
            names.extend([
                f"Everywhere-Windows-x64-Setup-{tag}.exe",
                f"Everywhere-Windows-x64-{tag}.zip",
                f"Everywhere-macOS-x64-{tag}.pkg",
                f"Everywhere-macOS-arm64-{tag}.pkg",
            ])
        paths = [self.assets / name for name in names]
        for path in paths:
            path.write_bytes(b"package")
        return paths

    def test_create_draft_generates_notes_only_for_canary(self):
        for tag, canary in (("v1.2.3-canary.1", True), ("v1.2.3", False)):
            with self.subTest(tag=tag), patch.object(github_release, "find_release", return_value=None), \
                    patch.object(subprocess, "run") as run:
                paths = self.make_packages(tag)
                github_release.attach_packages(self.repository, tag, self.assets, prerelease=canary, linux_only=False)
                args = run.call_args.args[0]
                self.assertEqual(args[:4], ["gh", "release", "create", tag])
                self.assertIn("--draft", args)
                self.assertIn("--latest=false", args)
                self.assertIn("--verify-tag", args)
                self.assertEqual("--generate-notes" in args, canary)
                self.assertEqual("--prerelease" in args, canary)
                self.assertEqual(set(a for a in args if isinstance(a, Path)), set(paths))

    def test_retry_only_uploads_assets_and_never_edits_handwritten_notes(self):
        tag = "v1.2.3-canary.1"
        for draft in (True, False):
            for linux_only in (True, False):
                release = {"draft": draft, "body": "Handwritten release notes"}
                with self.subTest(draft=draft, linux_only=linux_only), \
                        patch.object(github_release, "find_release", return_value=release), \
                        patch.object(subprocess, "run") as run:
                    paths = self.make_packages(tag, linux_only=linux_only)
                    github_release.attach_packages(self.repository, tag, self.assets, prerelease=True, linux_only=linux_only)
                    run.assert_called_once_with(
                        ["gh", "release", "upload", tag, "--clobber", "--repo", self.repository, *paths], check=True
                    )

    def test_incomplete_platform_output_never_reaches_github(self):
        tag = "v1.2.3"
        self.make_packages(tag)
        (self.assets / f"Everywhere-Windows-x64-Setup-{tag}.exe").unlink()
        with patch.object(subprocess, "run") as run, self.assertRaisesRegex(ValueError, "Windows-x64-Setup"):
            github_release.attach_packages(self.repository, tag, self.assets, prerelease=False, linux_only=False)
        run.assert_not_called()
