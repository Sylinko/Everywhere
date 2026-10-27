#!/usr/bin/env python3
"""Read release state, including drafts omitted by the release-by-tag endpoint."""

import json
import os
import subprocess


def find_release(repository, tag):
    result = subprocess.run(
        ["gh", "api", f"repos/{repository}/releases/tags/{tag}"],
        text=True,
        capture_output=True,
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


def main():
    release = find_release(os.environ["GITHUB_REPOSITORY"], os.environ["RELEASE_TAG"])
    draft = release["draft"] if release else True
    prerelease = release["prerelease"] if release else os.environ["PRERELEASE"] == "true"
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write(f"draft={str(draft).lower()}\nprerelease={str(prerelease).lower()}\n")


if __name__ == "__main__":
    main()
