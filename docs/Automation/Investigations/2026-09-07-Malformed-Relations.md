# Malformed Relation Boundary Verification

## Controlled Coverage

`AdversarialSnapshotTests` verifies one empty-container graph containing a shared child, a self-edge, and an ancestor cycle. Each of four identities expands once, each Enumerator is disposed once, useful content remains retrievable through its published ID after Snapshot disposal, and every element releases after the last turn is evicted. Two additional cases provide an endless repeated child: operation and child-count budgets independently terminate traversal without repeated expansion. The Automation suite passes 135 cases.

The follow-up text read exposed an incidental constructor bug in the former request struct: zero initialization left its maximum character count at zero and caused an indexing exception. The final element API removed that wrapper and accepts validated `offset` and `maxCharacters` parameters directly. This was a small corrective deviation from relation verification, not an identity-policy change.

## Reqable: Bounded Failure, Missing Native Identity

Read-only sampling on 2026-09-07 located Reqable PID 35116 with `EnumWindows`, despite `Process.MainWindowHandle` reporting zero. The visible window was `0x30B2A`; the probe neither altered nor closed it.

Three final production passes took approximately 21, 11, and 9 ms with limits of 128 nodes, 32 children per node, 512 platform operations, and a five-second cooperative deadline. Each returned two distinct nodes: the window and an empty Panel with `Child enumeration failed in the platform provider.` Window-ID follow-up queries succeeded. After Snapshot disposal and whole-turn eviction, target count returned to zero and the identity map returned to the one-entry acquisition baseline in every pass. This checks retained identity ownership, not every native allocation or a long-run memory plateau.

The independent native probe traversed 26 edges in each of two passes. Of those observations, 25 lacked usable cached RuntimeIds and 18 had nonempty names. These are observation counts, not proven distinct logical elements. No repeated valid RuntimeId was found in this sample; absence of identities prevents concluding that all anonymous relations are cycle-free.

The first-child-chain comparison returned:

| Depth | Cached RuntimeId | Direct `GetRuntimeId()` |
| --- | --- | --- |
| 0 | `42.199466` | `[42, 199466]` |
| 1 | `42.199450` | `[42, 199450]` |
| 2 | unavailable | empty array |
| 3 | unavailable | empty array |

Thus the sampled failure is not solely missing cache data. Useful deeper content exists, but cannot pass the current canonical-identity entry boundary. The production path safely stops that relation and exposes the failure. It does **not** provide useful Reqable document retrieval. This is an accepted best-effort limitation, not a blocker: retain bounded failure and local status without fallback IDs, pointer-based identities, or an anonymous-element architecture. Revisit only if broader application evidence justifies it.

Local ignored evidence is under `tests/Everywhere.Automation.Windows.Tests/bin/Debug/net10.0-windows10.0.19041.0/artifacts/external-window/`: `measurements.json`, per-pass compact prompts, and the native edge trace. Names/content remain local; this document preserves only the diagnostic counts and identity comparison. Reproduce with `ExternalWindowProbeTests` and an open Reqable window.

## WebView

The explicit native WebView Example Domain test also passes after removal of the global completeness flag. Its semantic Document remains visible in the production projection. The previously observed local parent conflict remains an accepted malformed-provider boundary, with evidence in [the parent-conflict investigation](2026-09-07-WebView-Parent-Conflict.md).
