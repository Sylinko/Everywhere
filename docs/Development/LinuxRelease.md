# Linux releases and AUR

Push a version tag (`vX.Y.Z` or `vX.Y.Z-canary.<numeric qualifiers>`) to start
the [desktop release workflow](../../.github/workflows/release.yml). Its matrix
builds Windows x64, macOS x64 and ARM64, and Linux x64 in parallel. If a build,
signing or packaging step fails, unfinished builds are cancelled and the release
job does not run. Linux ARM64 remains disabled pending native desktop acceptance.

The release job checks all seven files: the Windows installer and ZIP, both macOS
PKGs, and the Linux DEB, RPM and self-contained tar.zst. It creates a draft with
generated notes for a canary or a blank body for a stable release. If the release
already exists, it only uploads assets; retries preserve the body and publication
state. Review the draft and publish it when ready.

To rebuild just the Linux packages, set `tag` to the existing release tag and run
`gh workflow run release.yml --ref main -f tag="$tag"` in your shell.
This checks and uploads only the three Linux files; it does not rebuild or replace other platforms' packages.
It can also create a draft if the tag has no release yet.

RPM packages use `~canary` versions so stable versions sort after previews.
The RPM runtime dependencies have been validated on Fedora 44; other RPM
distributions require their own dependency and runtime acceptance. Packaging
preserves the published self-contained binaries without stripping them.

The build, checkout and release upload operate on the repository running the
workflow. In the upstream repository this is `Sylinko/Everywhere`; no fork or
contributor-owned release source is hardcoded.

`aur-publish.yml` optionally notifies the independent AUR maintainer after
`release: published`, covering stable and prereleases. The Linux build also
notifies after the Linux build succeeds, covering releases published before their
assets finished uploading. Drafts and releases without an x64 archive are skipped.
The notifier sends only the release tag; it does not push to AUR.

To opt in, the upstream maintainer sets `AUR_MIRROR_REPOSITORY` to the AUR
maintainer's receiver repository and configures `EVERYWHERE_AUR_DISPATCH_TOKEN`,
a fine-grained PAT with **Actions: write** on that destination. The receiver
must expose `sync-release.yml` on its main branch and accept `tag` and
`publish_aur` workflow inputs. Without a destination, notification is skipped
and the official release workflow succeeds. No scheduled polling is used.

The AUR maintainer's repository mirrors the official release binaries, generates
and archives checksummed PKGBUILD/.SRCINFO files, validates the selected package,
and pushes it to AUR using its own SSH credentials. Version pins and withdrawals
also belong to that downstream repository. No AUR SSH key is stored upstream.

`src/Everywhere.Linux/PKGBUILD.in` is a reference template for the downstream
maintainer. Its source repository is supplied during generation; the submitted
recipe may point to the maintainer's mirror while the application binaries still
originate from the official upstream release.
