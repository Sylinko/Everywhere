# Linux releases and AUR

`linux-release.yml` builds x64 and ARM64 from version tags (`vX.Y.Z` or
`vX.Y.Z-canary.<numeric qualifiers>`). It uploads DEB, AppImage and self-contained
tar.zst assets to a draft release. Wait for both matrix builds, then publish the
release as stable or prerelease. The manual build input retries an existing tag.

The build, checkout and release upload operate on the repository running the
workflow. In the upstream repository this is `Sylinko/Everywhere`; no fork or
contributor-owned release source is hardcoded.

`aur-publish.yml` optionally notifies the independent AUR maintainer after
`release: published`, covering stable and prereleases. The Linux build also
notifies after both builds succeed, covering releases published before their
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
