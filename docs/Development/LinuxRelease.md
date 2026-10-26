# Linux releases and AUR

`linux-release.yml` builds x64 and ARM64 from version tags (`vX.Y.Z` or
`vX.Y.Z-canary.<numeric qualifiers>`). It uploads DEB, AppImage and self-contained
tar.zst assets to a draft release. Wait for both matrix builds, then publish the
release as stable or prerelease. The manual build input retries an existing tag.

`aur-publish.yml` no longer pushes directly to AUR. A published release sends an
explicit workflow dispatch to `pasical/Everywhere-AUR`. The Linux build also
notifies after both builds succeed, covering a release published before its
assets finished uploading. Drafts and releases without an x64 archive are skipped.

Configure `EVERYWHERE_AUR_DISPATCH_TOKEN` in the source repository: a fine-grained
PAT restricted to `pasical/Everywhere-AUR`, with **Actions: write**. Optionally set
`AUR_MIRROR_REPOSITORY` to change the receiver. The independent repository owns
mirroring, pinned source checksums, package verification and AUR credentials.
No scheduled polling is used.

`src/Everywhere.Linux/PKGBUILD.in` is a reference template. The mirror generates the submitted
recipe using its public release URLs and actual per-architecture SHA-256 hashes.
The mirror tracks stable and Canary in the existing `everywhere-bin` package.
