# Everywhere Version Contract

Everywhere.Versioning contains SemanticVersion, SemanticVersionRange and UpdateChannel in the
Everywhere.Common namespace. It uses only the base class library. Everywhere.Abstractions and
Everywhere.Plugin.Packaging reference this assembly; SDK and runtime compatibility checks therefore
use the same implementation and type identity. Importing the plugin MSBuild SDK does not add its
tool dependencies as plugin compilation references.

## Versions and identity

Versions have one to four nonnegative integer components, an optional `-prerelease` suffix and
optional `+metadata`. Missing numeric components mean zero. The third component keeps the existing
`Build` property name; `Metadata` is the separate SemVer-style build metadata string. Numeric
components fit Int32. Git's `v` tag prefix is removed by the release/update caller, not the parser.

Prerelease and metadata labels are nonempty ASCII alphanumeric/hyphen segments separated by dots.
All prerelease labels are valid regardless of whether UpdateChannel recognizes them. A version
without a suffix follows every prerelease with the same numeric components. Prerelease segments
compare from left to right: numeric segments compare numerically without integer conversion;
numeric segments precede text; text compares ordinally without case. If shared segments compare
equally, the shorter label sequence precedes the longer one. Channel enum values never participate.

This retains Everywhere's short/four-component versions, case-insensitive comparison and accepted
numeric padding rather than claiming strict SemVer 2.0 input validation. In particular, numeric
prerelease padding is accepted and normalized. No package restore or update selection is performed.

Comparison, Equals, operators and hashing use the same identity. Metadata is ignored by all of
them, including exact range matches. ToString retains label spelling and metadata while formatting
at least three numeric components and omitting a zero revision. ToNormalizedString additionally
lowercases text labels, removes numeric label padding, and omits metadata. For example:

```text
Input:       01.2.0.0-PREVIEW.002+commit
ToString:    1.2.0-PREVIEW.002+commit
Normalized:  1.2.0-preview.2
```

Constructors are for trusted typed application values; TryParse/TryCreate validate external text
or numeric components. The existing SemanticVersion.Parse fallback behavior is retained. The
numeric Version conversion deliberately cannot preserve prerelease labels or metadata.

## Ranges

SemanticVersionRange.TryParse reports invalid input; Parse throws FormatException. Contains uses
the version ordering above, with no separate prerelease filter. Surrounding range/bound whitespace
is accepted. ToString produces canonical bounds.

| Declaration | Meaning |
|---|---|
| `1.2` or `[1.2,)` | At least 1.2.0 |
| `[1.2]` | Exactly the comparison identity 1.2.0 |
| `[1.2,2.0)` | At least 1.2.0, below 2.0.0 |
| `(1.2,2.0]` | Above 1.2.0, at most 2.0.0 |
| `(,2.0)` | Below 2.0.0 |
| `(,)` | Every version, explicitly requested |

Missing bounds must use parentheses. Reversed bounds and empty intervals are invalid. Empty
declarations, floating patterns and compound range expressions are not supported. Metadata in a
bound is ignored just as it is in ordinary comparison.

`[1.0,2.0)` includes `2.0.0-preview.1` because that version precedes `2.0.0`. Use
`[1.0,2.0.0-0)` to exclude every prerelease of 2.0.0 too. Stable Plugin API default ranges use this
upper bound to keep their same-major compatibility promise. Prerelease API defaults are exact.

## Application channels and migration

UpdateChannel identifies feeds, not precedence. Stable has no prerelease suffix; Canary recognizes
the first label `canary` without case; every other prerelease is Unknown. Numeric labels are not
interpreted as enum values. Existing enum numeric values remain unchanged for settings persistence.
Adding a recognized feed must not alter comparison.

Translations remain in Everywhere.Abstractions. The existing settings generator's enum key convention
uses `UpdateChannel_Unknown`, `UpdateChannel_Canary` and `UpdateChannel_Stable`, so the version library
does not need localization attributes or dependencies.

Historical Git tags and settings migration declarations keep their string representation and order.
Migration selection continues to use versions greater than the persisted version, in ascending order.
No historical version rewriting or settings migration is required for moving the types into this
assembly. There are no duplicate source-compiled types or forwarding shims.