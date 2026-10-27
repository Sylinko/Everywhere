#!/usr/bin/env python3
"""Dispatch a public Linux release to the independent mirror; no AUR key is used here."""

import json
import os
import re
import subprocess
from github_release import find_release


def main():
    source = os.environ["GITHUB_REPOSITORY"]
    tag = os.environ["RELEASE_TAG"]
    target = os.environ.get("AUR_MIRROR_REPOSITORY", "").strip()
    if not target:
        print("AUR notification is not configured; the official release remains available.")
        return
    if not re.fullmatch(r"v\d+\.\d+\.\d+(?:-canary\.[0-9.]+)?", tag):
        raise ValueError("Unsupported release tag.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", target):
        raise ValueError("Invalid mirror repository.")
    release = find_release(source, tag)
    if release is None or release["draft"]:
        print("Release is still a draft; publish it to trigger the mirror.")
        return
    required = f"Everywhere-Linux-x64-{tag}.tar.zst"
    if not any(
        asset["name"] == required and asset["state"] == "uploaded" and asset["size"] > 0
        for asset in release["assets"]
    ):
        print("No completed Linux x64 archive yet; the Linux build will notify again when complete.")
        return
    token = os.environ.get("EVERYWHERE_AUR_DISPATCH_TOKEN")
    if not token:
        raise ValueError(
            "Configure EVERYWHERE_AUR_DISPATCH_TOKEN: "
            "a fine-grained PAT with Actions:write on the mirror repository."
        )
    environment = {**os.environ, "GH_TOKEN": token}
    payload = {"ref": "main", "inputs": {"tag": tag, "publish_aur": True}}
    subprocess.run(
        [
            "gh", "api", f"repos/{target}/actions/workflows/sync-release.yml/dispatches",
            "--method", "POST", "--input", "-",
        ],
        input=json.dumps(payload),
        text=True,
        check=True,
        env=environment,
    )
    print(f"Dispatched {tag} to {target}.")


if __name__ == "__main__":
    main()
