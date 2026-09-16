# Native Text Paging and Real-Web Retrieval

## Controlled Multiline Editors

`NativeTextPagingTests` uses the existing `document-editor` scenario with seed 114514, finds its largest observed TextEdit, and edits only the TestApp it launched. It writes 200 numbered lines containing Latin, Chinese, Arabic, emoji, and a combining mark. Each provider exposes a 12,998 UTF-16-unit stream. Newline normalization is allowed only when comparing the submitted text with the native stream; page concatenation is compared exactly with that native stream.

| Host | Pages at limit 257 | Total paging time in this run | Read while UI thread suspended | After resume |
| --- | --- | --- | --- | --- |
| WinForms | 51 | 36 ms | Returned successfully in 1 ms | Same-offset read succeeds |
| Avalonia | 51 | 15 ms | Timeout after 10.9 s | Same-offset read succeeds |

Both page sequences reached the end and reconstructed the full stable native stream exactly. A prefix insertion between reads was followed by an overlapping read at offset 250, matching a fresh observation of the modified stream. The suspension test deliberately reports success or normalized failure: a proxy can respond without the application's UI thread. These are small local observations, not a performance guarantee or proof that arbitrarily large prefixes are cheap. Per-page timings are saved in the Windows test output's ignored `artifacts/native-text-{platform}.json`.

Run with `dotnet test tests/Everywhere.Automation.Windows.Tests --filter FullyQualifiedName~NativeTextPagingTests`. The explicit tests own and close both editor processes, and resume the UI thread in a finally block.

## Native WebView MCP Journey

The Probe was driven through Streamable HTTP with one persistent visual turn per website. Each journey acquired the root with 128 admitted nodes and a 4096 approximate-token projection budget, followed the Document ID with a 32-node/2048-budget structural query, read numeric text pages, and queried a returned child ID. URLs and full responses are recorded in the ignored `tests/Everywhere.Automation.WebView.Probe/artifacts/text-journey-20260907` directory.

| Page | Published targets in initial query | Offscreen flags | Initial text continuation |
| --- | --- | --- | --- |
| MDN JavaScript Reference | 113 | 47 | Three 2048-unit pages; last page has no next |
| GitHub microsoft/terminal | 113 | 30 | Four 2048-unit pages; more remains |
| Wikipedia Accessibility | 117 | 39 | Four 2048-unit pages; more remains |

All three exposed semantic Documents and usable child IDs. Follow-up inspection of Wikipedia's offscreen link named `public transport system in Curitiba` returned its retained identity and URL. Its two-member introductory Composite could be read independently. A direct Document read at offset 32768 returned substantive article text. Continuing from 8192 with 16384-unit pages returned next offsets 24576, 40960, and 57344, then a final page without next or status. Reading the previous GitHub Document after navigation returned only the current unsupported-text status, without stale structural diagnostics.

The native turn smoke check also passed: an unscoped call completes a temporary turn, more than eight calls reuse a persistent turn, an early Document ID remains readable, and explicitly starting the next turn advances history once.

These results establish offscreen reachability and usable continuation. They do not establish an A/B ranking improvement from the 0.9 multiplier, identical output from changing websites, or exhaustive child traversal. The MDN and Wikipedia final pages are current observations, not immutable-document proofs.

## Potential Projection Optimization

A narrow `parent,child` query around the offscreen Wikipedia link reintroduced a long Document ancestor preview. The anchor was retained and the budget respected, but ancestor text dominated the returned context. If this repeats in actual use, consider reducing ancestor previews for anchor-focused follow-up queries while preserving the ancestor skeleton and the existing one-time relevance weighting. Do not introduce a new pipeline stage or change defaults based on this one observation. Callers can already choose `directions=none` when only the target is needed.

### Conservative Duplicate-Preview Experiment

The experiment (subsequently removed in favor of progressive body allocation) suppressed only fully covered, non-core ancestor bodies, with whitespace-only normalization and render-validated leaf dependencies. It does not assign a smaller allowance to ancestors or change traversal weights. Controlled tests cover exact and whitespace-only matches, partial/case-mismatched content preservation, continuation-hint overhead, retained-ID text lookup after Snapshot disposal, and restoration when the covering body is budget-limited. A generous-budget long duplicate is rendered once; under a tight budget, restoration returns the ancestor to ordinary budget handling rather than promising the full text will fit.

The live follow-up selected the first exposed Hyperlink from a 128-node/4096-budget root query, then requested `parent,child` at 32 nodes/2048 budget and read the returned Document ID. Results are in ignored `WebView.Probe/artifacts/ancestor-preview-20260907`:

| Site | Initial represented targets | Follow-up output characters | Ancestor body characters in serialized output |
| --- | --- | --- | --- |
| MDN JavaScript Reference | 112 | 4894 | 4189 |
| GitHub microsoft/terminal | 113 | 4939 | 4141 |
| Wikipedia Accessibility | 117 | 4654 | 4136 |

All Document reads returned a first page and `next=128`. None of these ancestor bodies qualified for removal. Character counts include escaping and are not native text lengths or exact token counts. These are live smoke observations, not same-Snapshot A/B measurements or evidence of improved retrieval cost. The original long-ancestor concern remains a separate allocation question; do not relax exact coverage to fit these samples. The native probe run preceded the final tiny-preview overhead guard; that guard only skips additional candidates and does not affect these long retained bodies.

The first sandboxed host startup timed out before producing TestApp readiness; logging that failure also hit Windows EventLog permissions. Running the same probe outside the sandbox succeeded. No host workaround or production platform change was introduced.
