#!/usr/bin/env python3
"""Attach desktop packages without rewriting an existing release."""

import argparse
import json
import os
from pathlib import Path
import subprocess


def find_release(repository, tag):
    result = subprocess.run(
        ["gh", "api", f"repos/{repository}/releases/tags/{tag}"],
        text=True,
        capture_output=True,
        timeout=120,
    )
    if result.returncode == 0:
        return json.loads(result.stdout)
    if "HTTP 404" not in result.stderr:
        raise RuntimeError(result.stderr)
    pages = subprocess.run(
        ["gh", "api", f"repos/{repository}/releases?per_page=100", "--paginate", "--slurp"],
        check=True,
        text=True,
        capture_output=True,
        timeout=120,
    )
    return next(
        (
            release
            for page in json.loads(pages.stdout)
            for release in page
            if release["tag_name"] == tag
        ),
        None,
    )


def attach_packages(repository, tag, directory, *, prerelease, linux_only):
    assets = [directory / f"Everywhere-Linux-x64-{tag}.{ext}" for ext in ("deb", "rpm", "tar.zst")]
    if not linux_only:
        assets.extend([
            directory / f"Everywhere-Windows-x64-Setup-{tag}.exe",
            directory / f"Everywhere-Windows-x64-{tag}.zip",
            directory / f"Everywhere-macOS-x64-{tag}.pkg",
            directory / f"Everywhere-macOS-arm64-{tag}.pkg",
        ])

    # An artifact upload can succeed even when it contains only part of a platform's output.
    for asset in assets:
        if not asset.is_file() or asset.stat().st_size == 0:
            raise ValueError(f"Missing or empty release asset: {asset.name}")

    if find_release(repository, tag) is None:
        # Notes belong to the draft's creation, never to a build retry.
        command = ["gh", "release", "create", tag, "--verify-tag", "--draft", "--latest=false", "--title", tag]
        if prerelease:
            command.extend(["--prerelease", "--generate-notes"])
        else:
            command.extend(["--notes", ""])
    else:
        command = ["gh", "release", "upload", tag, "--clobber"]

    subprocess.run([*command, "--repo", repository, *assets], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--linux-only", action="store_true", help="Attach only the three Linux packages.")
    args = parser.parse_args()
    attach_packages(
        os.environ["GITHUB_REPOSITORY"], os.environ["RELEASE_TAG"], Path("release-assets"),
        prerelease=os.environ["PRERELEASE"] == "true", linux_only=args.linux_only,
    )
