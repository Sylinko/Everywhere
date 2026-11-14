import importlib.util
import json
import os
import tempfile
import textwrap
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
    def test_existing_release_outputs_preserve_publication_state(self):
        for draft, prerelease in ((True, False), (False, True), (False, False)):
            with self.subTest(draft=draft, prerelease=prerelease), tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "output"
                with patch.dict(os.environ, {"GITHUB_REPOSITORY": "Sylinko/Everywhere", "RELEASE_TAG": "v1.2.3",
                                             "GITHUB_OUTPUT": str(output)}), \
                        patch.object(github_release, "find_release", return_value={"draft": draft, "prerelease": prerelease}):
                    github_release.main()
                self.assertEqual(output.read_text(),
                                 "exists=true\ndraft=%s\nprerelease=%s\n" % (str(draft).lower(), str(prerelease).lower()))

    def test_missing_release_outputs_create_a_draft_in_the_selected_channel(self):
        for prerelease in ("true", "false"):
            with self.subTest(prerelease=prerelease), tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "output"
                with patch.dict(os.environ, {"GITHUB_REPOSITORY": "Sylinko/Everywhere", "RELEASE_TAG": "v1.2.3",
                                             "GITHUB_OUTPUT": str(output), "PRERELEASE": prerelease}), \
                        patch.object(github_release, "find_release", return_value=None):
                    github_release.main()
                self.assertEqual(output.read_text(), "exists=false\ndraft=true\nprerelease=%s\n" % prerelease)

    def test_upload_step_only_mutates_assets_of_an_existing_release(self):
        workflow = (SCRIPTS.parent / ".github/workflows/linux-release.yml").read_text(encoding="utf-8-sig")
        upload = workflow.split("- name: Upload Linux assets without changing publication state", 1)[1]
        script = textwrap.dedent(upload.split("run: |\n", 1)[1].split("\n  notify-aur:", 1)[0])
        for exists, prerelease in (("true", "false"), ("true", "true"), ("false", "false"), ("false", "true")):
            with self.subTest(exists=exists, prerelease=prerelease), tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                log = directory / "arguments.json"
                executable = directory / "gh"
                executable.write_text("#!" + sys.executable + "\nimport json, os, sys\n"
                                      "with open(os.environ['ARGUMENT_LOG'], 'w') as stream:\n"
                                      "    json.dump(sys.argv[1:], stream)\n")
                executable.chmod(0o755)
                assets = directory / "release-assets"
                assets.mkdir()
                for architecture in ("x64", "arm64"):
                    for extension in ("deb", "rpm", "tar.zst"):
                        (assets / ("Everywhere-Linux-%s-v1.2.3.%s" % (architecture, extension))).touch()
                environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ["PATH"],
                                   RELEASE_EXISTS=exists, PRERELEASE=prerelease, RELEASE_TAG="v1.2.3",
                                   VERSION="1.2.3", GITHUB_REPOSITORY="Sylinko/Everywhere", ARGUMENT_LOG=str(log))
                subprocess.run(["bash", "-eu", "-c", script], cwd=directory, env=environment, check=True)
                arguments = json.loads(log.read_text())
                self.assertEqual(arguments[:3], ["release", "upload" if exists == "true" else "create", "v1.2.3"])
                self.assertEqual(len([value for value in arguments if value.startswith("release-assets/")]), 6)
                if exists == "true":
                    self.assertIn("--clobber", arguments)
                    self.assertNotIn("--draft", arguments)
                    self.assertNotIn("--prerelease", arguments)
                    self.assertNotIn("--latest=false", arguments)
                else:
                    self.assertIn("--draft", arguments)
                    self.assertIn("--latest=false", arguments)
                    self.assertEqual("--prerelease" in arguments, prerelease == "true")
