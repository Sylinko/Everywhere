# Query Operations and Scan Image Ownership

## Query Boundary

VisualQuery is a Context-bound instance. ExecuteAsync resolves a retained integer target or accepts an acquired target for host/probe use. BuildAsync accepts attachment/debugger anchors together with the same complete VisualQueryRequest used by ExecuteAsync. ReadText resolves an integer target in the same Context; the former public static VisualTextQuery entry point is removed and its text implementation is part of VisualQuery.

VisualQueryRequest is the single source of truth for traversal directions, relation offset, admitted node limit, and requested result fields such as bounding boxes. VisualContextPromptOptions contains only local token-budget and compression policy. Target, platform-default-root, and pre-publication-anchor RPC operations all carry the complete query contract instead of duplicating selected fields.

The instance neither owns/disposes Context nor creates/completes a turn. Callers continue to serialize Context operations. Structural results use VisualQueryResult (final Content and RepresentedTargetCount), not the lower-level builder's result type. QoS, final text, native capture policy, and target identity remain unchanged.

## Optional Capture Production

Snapshotter synchronously reports each newly admitted TopLevel through onTopLevelObserved. This is a narrow internal observation hook carrying a borrowed result, not a generic per-node replay or a cross-process protocol. Its consumer must not perform native work. VisualQuery collects results only when a capture receiver is configured.

After traversal, the still-owned Snapshot retains observed windows during serial CaptureAsync calls. Existing offscreen animation suppression is preserved; offscreen traversal and ordinary capture capability remain available. Queries that never observe a TopLevel do not secretly traverse parents for an effect. Screenshot failures are logged and skipped; caller cancellation propagates, disposes any undelivered image, and prevents final publication. Capture preparation precedes builder publication, so optional capture latency is part of the asynchronous call; no worker dispatcher is introduced.

There is one asynchronous capture receiver, not a multicast owning event. On normal return it owns the capture, including immediate disposal when rejecting an optional effect. On exception before transfer the query disposes it. The receiver sees only `IVisualElementCapture`; no Element, Retention, or Context crosses the image boundary.

When the query runs in the Automation Host, the query result and zero or more scan captures share one query-associated RPC stream. Each image entry wraps the same header/chunk capture-frame contract used by explicit capture operations; the query protocol does not define a second bitmap transport. A completed image is transferred to the scan receiver as soon as its declared byte count arrives, while the final result remains the terminal query frame. The Host keeps the Context operation serialized while the Main process drains the stream, so transport backpressure also bounds retained image memory. Closing or canceling the stream cancels the query operation. The ordinary request/response methods remain available for callers that do not request scan feedback.

Scan captures use an Alpha8 transport payload because the shader reads only alpha. The Host extracts alpha directly from native rows while producing chunks and never retains a second full-size mask. Opaque source formats produce `255` for every pixel. Attachment previews and explicit screenshot tools continue to use the full-color capture protocol.

## Animation Consumer

ScanEffectScope owns only a bounded queue of captures. Its four-entry queue bounds retained image count instead of retaining the former thousand-element queue as images. `AddCapture` disposes captures rejected by cancellation, completion, or capacity. `Complete` transfers draining and cleanup to the asynchronous consumer; `Dispose` before `Complete` drains abandoned production. External cancellation during consumption disposes the current capture and remaining queue.

Conversion creates independent Bitmap/SKImage storage; captures are released when their pixels are no longer needed. Bounds determines desktop placement, while Size/Stride/Format describe image memory. Effects perform no Parent queries, native capture, or Context retention. Pick callers prepare captures before calling the effect; optional capture failure restores attachment visibility.

One VisualEffectImage owner is shared across screen particles and scan drawing operations. Each participant retains a reference and releases exactly once; the producer releases its initial reference after distributing images. The last release disposes the image. Particle recycle, failed spawn, and effect-window closure release their references. This replaces independent wrappers around the same SKImage and unowned pick bitmaps.

## Verification

Focused tests cover disabled capture, repeated TopLevel observation, receiver failure, cancellation before delivery/publication, canceled-scope rejection, shared-image final release, and Alpha8 query streaming across real duplex RPC. Existing query/text tests use instance entry points and normal target publication. Native multi-screen rendering, cancellation during playback, and macOS geometry remain desktop-validation work; compilation and ownership tests do not establish visual correctness.
