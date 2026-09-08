# Native WebView Parent Conflict

## Evidence

The Windows Avalonia NativeWebView host loading Example Domain produced a conflicting-parent status during child-only Snapshot traversal. The `diagnose_topology` MCP tool samples Content View directly through Interop, before the production identity map and Snapshotter. It retains original COM references and re-queries Parent on both references when a RuntimeId repeats. Each pass is limited to 256 edges; two passes share a 30-second cooperative deadline and the standard native timeouts.

Two complete passes on 2026-09-07 each observed 17 edges. Both reproduced:

| Observation | Parent RuntimeId | Child RuntimeId | Raw element interface pointer | Immediate Parent query |
| --- | --- | --- | --- | --- |
| Pass 0, ordinal 3 | `42.591492` | `42.788892` | `0x15282CEE630` | `42.591492` |
| Pass 0, ordinal 4 | `42.198702.4.27545501` | `42.788892` | `0x15282CEEFC0` | `42.198702.4.27545501` |
| Pass 1, ordinal 3 | `42.591492` | `42.788892` | `0x15282CEE630` | `42.591492` |
| Pass 1, ordinal 4 | `42.198702.4.27545501` | `42.788892` | `0x15282CEE750` | `42.198702.4.27545501` |

At each duplicate, querying Parent again on the first retained reference still returned `42.591492`. These are interface-pointer observations, not an IUnknown identity comparison. Pointer and RuntimeId values are run-local evidence, never reusable locators.

The local full artifact is `tests/Everywhere.Automation.WebView.Probe/artifacts/closeout-20260907/topology.json`. It is intentionally ignored by Git; the table preserves the relevant evidence in versioned documentation. `Verify-Retrieval.ps1` reproduces the diagnostic against a fresh Probe server.

## Interpretation and Review Boundary

The inconsistent parent relation exists before canonicalization. The evidence does not support blaming Snapshot relation bookkeeping or suppressing the current status. It does not yet distinguish host/provider composition, navigation-path-specific UIA objects, and an underlying RuntimeId collision. It does not establish that the two references represent different logical controls.

The accepted boundary is one canonical element per RuntimeId and Snapshot's first accepted parent edge, with local conflict status. Malformed provider relations do not require a new identity model. Snapshot no longer makes a global completeness claim. Host/provider composition remains optional investigation; any future proposal to qualify identity by parent or retain multiple native representatives requires separate evidence and review.

[Microsoft's TreeWalker contract](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtreewalker-getfirstchildelementbuildcache) describes view filtering and live-tree mutation. It does not by itself explain two concurrently retained references returning different parents in this probe.
