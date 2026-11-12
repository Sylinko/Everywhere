# Independently published dependency packages

Everywhere consumes the Porta.Pty, EverythingNetCore and MessagePack forks from
`https://nuget.sylinko.com/v3/index.json`. Their source repositories are maintained
separately and are absent from the application build graph and solution project
lists. Application release workflows fetch only the remaining source submodules.
OfficeCLI is packaged independently from an unmodified upstream
submodule in `Sylinko/OfficeCLI`; the application consumes its package as well.

For the current .NET 11 source upgrades, validation results and publication order, see
[the dependency upgrade handoff](DotNet11Upgrade.md). The dated sections below retain
the evidence from the original package extraction.

## Published adoption on 2026-11-12

The application now consumes Avalonia.Native 12.1.6 and Porta.Pty/Porta.Pty.Native
999.20261111.4 from the online Sylinko feed. Actual cache source metadata and archive
hashes were verified against their release manifests. The Native cache entry from
local validation was backed up before restoring the published bytes. See the
[upgrade record](DotNet11Upgrade.md#published-package-adoption-on-2026-11-12) for
all adopted producer versions and validation results. CSharpMath is an unused
application dependency and has been removed; LiveMarkdown.Math uses RaTeX. The
LiveMarkdown upgrade is prepared on main in `/Volumes/T7/Source/LiveMarkdown.Avalonia`
for the user to commit. The draft table and dated
extraction checks below retain the original investigation context.

## Package identity and versions

| Producer repository | Package IDs | Local draft version |
| --- | --- | --- |
| Sylinko/Porta.Pty | Porta.Pty, Porta.Pty.Native | 999.0.0 |
| Sylinko/EverythingNetCore | EverythingNetCore | 999.0.0 |
| Sylinko/MessagePack-CSharp | MessagePack, MessagePack.Annotations, MessagePackAnalyzer | 999.0.0 |
| Sylinko/OfficeCLI | OfficeCLI | 999.0.0 |

Original IDs are intentional: a third-party dependency on MessagePack must join
the same NuGet dependency graph instead of bringing a second package that
contains an assembly named MessagePack. The package version identifies the fork;
assembly names and MessagePack's existing assembly version/signing key remain
unchanged. An identical ID does not guarantee API or binary compatibility with
every third-party consumer of the upstream library.

The exact package IDs in `nuget.config` map to the Sylinko feed. Exact-ID mappings
take precedence over nuget.org's `*` mapping, including for transitive packages.
Source mapping controls restore source selection; it does not filter every IDE
metadata query. Already extracted global-cache packages can bypass source
lookup, so never reuse an existing ID/version for different binary contents.

CI generates release versions as `999.YYYYMMDD.<github.run_number>`, such as
`999.20261001.42`. The date is the original workflow run's creation date in UTC;
reruns retain both the date and run number. Each producer has its own counter.
All packages from one producer run share one version. Keep one publishing
workflow for each package ID, and record the upstream version and commit in
source history and release metadata rather than encoding them in the package
version. These three-component numeric release versions follow standard SemVer.

`999.0.0` is a deterministic local draft version, not a release version. Everywhere's
central entries use published producer versions; temporary validation can override them
with newly packed drafts. After each real CI release and feed deployment, update the
corresponding central versions. Producer counters and dates are not known before
their first runs, so no online version manifests or guessed CI pins are created.

A high version satisfies ordinary upstream minimum-version requirements; it
does not satisfy an exact upstream range or an upper bound below the fork
version, and does not prove API compatibility. Central package management
organizes version declarations; a bracketed range can require one exact version.
Existing package source mappings continue to select the
Sylinko feed. Pure transitive dependencies are not automatically pinned merely
by adding a central version entry.

PackageVersion is passed independently of Version, AssemblyVersion and
FileVersion. Porta retains assembly/file version 1.0.0.0, EverythingNet retains
2.0.0.0, and MessagePack retains assembly version 1.0.0.0 and file version 3.1.3.0.
This prevents the large date component from entering assembly/file identity.

## OfficeCLI executable package

The wrapper repository is maintained at `D:\Source\CSharp\OfficeCLI` locally.
It builds the upstream managed executable once and packages platform apphosts
for win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64. Dependencies are taken
from the upstream project's PackageReferences and flow through NuGet; no second
runtime or copies of third-party DLLs are embedded in the package.

The package's buildTransitive targets copy the apphost and create the two OfficeCLI
sidecars from the consuming executable's SDK-generated dependency manifest and
runtime configuration. The publish manifest is copied after trimming. This keeps
the CLI process on the actual consumer dependency graph and bundled runtime.
OfficeCLI stays a separate process and retains its real executable path for
resident/watch child processes. Its assembly is rooted for trimming by the package.
Single-file and NativeAOT consumers are unsupported.

macOS explicitly enables dependency manifest generation (disabled by default in
the Apple SDK) and keeps the MonoBundle copy, executable permissions, and signing
steps. Bundle sidecars use the package's `OfficeCliDepsFile` and
`OfficeCliRuntimeConfig` paths, selecting the final publish files after trimming.
Windows and Linux use the package's normal build/publish copying.
`OfficeCLIPlugin` sets `OFFICECLI_NO_AUTO_INSTALL=1` and `OFFICECLI_SKIP_UPDATE=1`
on the child process to disable automatic installation and background updates.
Explicit maintenance commands are not disabled by those environment variables.

The OfficeCLI central version refers to a published release. To consume a new source
revision, push the reviewed wrapper, run its manual Publish NuGet workflow on main,
merge the resulting feed PR and update the consumer to the generated version. The
workflow uses `SYLINKO_NUGET_FEED_TOKEN`. Do not publish a draft or guess a CI run number.
The upstream commit remains pinned in the wrapper's submodule; updates require
reviewing and committing that pointer before publishing another package version.

Local validation (2026-10-02) used upstream 1.0.153 at
`7d8f777a34cef4e409ef6ca4cc22c532fc49ee07`. A package-only consumer passed RID-less
build and trimmed self-contained win-x64 publish, Word/Excel/PowerPoint creation,
Word resident edit/read, and a System.IO.Packaging dependency override. The actual
Everywhere Windows Debug build and resident edit/read also passed. All five
apphost binary formats/architectures were inspected, and feed registration
validation passed. Linux/macOS execution is covered by the wrapper CI; actual
Everywhere macOS Bundle assembly/signing and online restore await user validation.

## Porta platform assets

Porta retains its compile-time OS switches. Its managed implementations are
AnyCPU and differ by OS, while its native libraries differ by OS and architecture:

```text
Porta.Pty
  ref/net11.0/Porta.Pty.dll
  runtimes/win/lib/net11.0/Porta.Pty.dll
  runtimes/linux/lib/net11.0/Porta.Pty.dll
  runtimes/osx/lib/net11.0/Porta.Pty.dll
  buildTransitive/Porta.Pty.targets
  buildTransitive/hosts/{x64,arm64}/OpenConsole.exe

Porta.Pty.Native
  runtimes/{linux-x64,linux-arm64}/native/libporta_pty.so
  runtimes/{osx-x64,osx-arm64}/native/libporta_pty.dylib
```

NuGet uses the common reference assembly for compilation and selects the
compatible managed runtime asset on build/publish. `VerifyManagedApi` checks that
all three implementations expose the same public types and members before pack.
The pack script uses representative x64 RIDs only to select OS compilation
switches; the resulting managed DLLs are AnyCPU. Native binaries are built
separately. The initial supported application RIDs are win-x64, linux-x64,
linux-arm64, osx-x64 and osx-arm64; Windows ARM64/x86 are not independently tested
by this release workflow.

Porta.Pty depends on Porta.Pty.Native and Microsoft.Windows.Console.ConPTY.
ConPTY's native DLLs flow through the latter dependency, but its `build/` helper
copying does not flow transitively. Porta therefore carries the corresponding
x64 and ARM64 OpenConsole hosts with `buildTransitive/` copying rules, preserving
ConPTY's host-directory layout. The ARM64 host also supports x64 processes under
Windows ARM64 emulation.

The release workflow builds Linux native libraries on Ubuntu 22.04, including
ARM64 through its cross compiler, and builds both macOS architectures with a
deployment target of macOS 12.0. Both RID-less and RID-specific package consumers
run an actual PTY shell smoke test from a RID-specific publish on each supported
platform before release. Windows additionally checks RID-less consumption to
cover the second output layout; Unix jobs do not repeat that build.

## EverythingNetCore and MessagePack

EverythingNetCore carries both x86 and x64 Everything.dll/Everything.exe under
`runtimes/<rid>/native/`. A RID-specific publish copies the selected native files
to the output root. `EverythingState` checks that layout first and then checks
the runtime subdirectory used by RID-less builds. Its smoke test verifies both
layouts and invokes the native DLL without installing or starting a service.

MessagePack's runtime package depends on the forked annotations and analyzer
packages. MessagePackAnalyzer contains the source generator and code fixes under
`analyzers/roslyn4.3/cs/`. Everywhere's projects that previously referenced the
source generator explicitly now reference MessagePackAnalyzer with
`PrivateAssets="all"`. The producer smoke test uses only generated formatters,
so it fails if the generator is missing from the package. These minimal smoke
projects restore exact versions from the newly packed local artifacts using
private package caches; they do not use producer project references or run the
upstream library test suites.

## Original first-release sequence (2026-10-01)

This records the original extraction. For subsequent updates, use the current independent
producer checkouts and the publication order in [the upgrade handoff](DotNet11Upgrade.md#publication-and-consumption-order).

1. Commit and push the six `bucket/<lower-id>/package.yml` registrations in
   `Sylinko/nuget-feed`. They explicitly permit the original package IDs, matching
   the existing Microsoft.ML feed convention. Registration alone publishes no
   package versions.
2. Commit the changes inside each producer checkout and push to its Sylinko
   repository. All three current submodule checkouts use detached HEAD; create
   a publishing branch before committing. Check the configured remote first: the current MessagePack
   checkout's origin uses the older `NodisAI/MessagePack-CSharp` URL, while
   `.gitmodules` and the feed registration name `Sylinko/MessagePack-CSharp`.
3. Configure `SYLINKO_NUGET_FEED_TOKEN` in each producer repository, with the same
   capabilities used by the Microsoft.ML workflow. Run **Release Sylinko NuGet
   Packages** through workflow_dispatch with no version input, or push a source
   tag such as `publish-2026-10-01`. The workflow reads its original creation
   timestamp through the GitHub API and generates the package version. It creates
   a release tagged `nuget/<producer>/<version>` through `Sylinko/nuget-feed@v1`.
   A guard rejects replacing a release that already contains assets, because the
   existing feed action uploads with `--clobber`. If the action failed after
   uploading assets, keep them and start a new run for a new version; inspect
   the original feed PR if one was already created. The feed service is unchanged.
4. Review and merge the generated feed PR for each producer. The action records
   the source commit, workflow, release assets and hashes. Do not fabricate
   version manifests before real release assets exist.
5. Wait for feed deployment, replace the six local draft central pins with their
   respective actual CI versions, then commit/push Everywhere's package-reference
   migration and the producer submodule commit pointers. A clean CI restore
   requires the new versions to be available on the feed first.

Local validation used generated `.nupkg` files through a temporary local source;
the production `nuget.config` contains no local source fallback. The source
submodules can be removed separately once the first published versions have
been verified and the producer commits are safely pushed.

Local smoke testing installs the initial package versions into the global NuGet
cache. For the first check of the real published packages, use a fresh cache so
that the draft local binaries cannot satisfy restore without contacting the feed:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet restore Everywhere.Windows.slnx -r win-x64 --packages artifacts/feed-validation-cache
```

## Feed discovery findings (2026-10-01)

The current feed generator advertises only `PackageBaseAddress/3.0.0` and
`RegistrationsBaseUrl/3.6.0`. Flat-container version lists and package downloads
support known package IDs/versions. There is no `SearchQueryService` or
`SearchAutocompleteService`, which explains the missing package-browse/search
experience in Rider; the client has no advertised discovery endpoint. Exact
Rider request behavior was not traced in this migration.

Registration metadata is also minimal: it lacks dependencyGroups and normal
descriptive fields, and advertises catalog-entry URLs that are not generated.
These omissions can limit IDE metadata and dependency inspection even though
known-version restore succeeds through the nuspec/package paths.

A later feed change should add search with empty-query browsing, pagination,
prerelease and SemVer filtering; autocomplete for package IDs and versions; and
complete registration metadata. These are protocol capabilities and can fit
the existing Cloudflare deployment. This migration adds only package manifests
and leaves the service implementation unchanged.

Sources:

- [NuGet managed and native asset selection](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages)
- [Package Source Mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping)
- [NuGet search API](https://learn.microsoft.com/en-us/nuget/api/search-query-service-resource)
- [NuGet autocomplete API](https://learn.microsoft.com/en-us/nuget/api/search-autocomplete-service-resource)

## Verification and limits

The date-based version update was locally packed as `999.20261001.42` for all
six packages. YAML and embedded PowerShell syntax, original-run UTC date
handling, rerun stability, invalid run numbers, and release asset guards passed
local probes. Further metadata and consumer verification was stopped at the
user's request in favor of testing real feed publication. Temporary migration
probes, package archives and producer build outputs were cleaned up.
Minimal smoke projects were subsequently restored. Windows PTY startup and
Everything native calls passed in both RID-less and win-x64 published outputs;
MessagePack's generated payload formatter plus built-in primitive formatters
passed a round trip through a NuGet-only consumer. No reflection fallback or
upstream test suite is used. Unix smoke jobs now only publish and execute once
per RID. Linux/macOS execution remains for real producer CI. Local verification
outputs were cleaned up while preserving the smoke-test source files.
The broader checks below describe the earlier packaging migration.

Local checks validate all six package IDs, dependencies, and mandatory package
assets. All three managed Porta implementations build and share a public API.
Windows package smoke tests exercise Porta PTY startup and Everything native
loading in both output layouts; MessagePack's generated-formatter round trip
passes. Existing ProcessIsolation tests pass 64/64, and Terminal tests pass 144
with 12 platform/feature skips.

The complete Windows Debug build, including Watchdog NativeAOT publishing, passes
with zero errors. It retains NuGet audit and AOT/trim warnings. These checks use
local packages; real online feed deployment remains to be verified.

The previous local MessagePack prerelease produced NU1902/NU1903 audit warnings
against the upstream package's advisory data. Date-based fork versions can lie
outside those advisory ranges; the absence of warnings is not evidence that the
modified source is fixed. Advisory applicability needs separate assessment
against the upstream source revision. Audit warnings are not suppressed.

Local native Porta packaging uses the four binaries already tracked in the
producer repository; Linux/macOS native compilation and execution, GitHub
workflow execution, and online restore of the new release versions require the
first producer CI runs. The workflow rebuilds the native libraries before pack.

A temporary HTTP V3 feed reproduced the current service's flat-container and
minimal registration resources, using an isolated package cache. A consumer with
only Porta.Pty, EverythingNetCore and MessagePack references restored all their
transitive packages, loaded the source generator, completed the serialization
round trip, loaded Everything.dll and ran a Windows PTY shell. This confirms that
the tested SDK restore reads dependency data from downloaded packages and their
nuspecs despite the registration omissions; it does not establish IDE metadata
compatibility.
