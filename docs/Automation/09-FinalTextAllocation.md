# Final Visual Text and Progressive Allocation

## Decision and Scope

Visual tools return final strings. Snapshot remains the native observation phase; the visual Builder owns normalization, Composite projection, admission, allocation, rendering, and target publication. No public Planner or deferred Chat-side visual allocation is introduced. This decision supersedes earlier requirements to return a structured, locally limited PromptNode.

PromptNode remains an internal syntax/escaping mechanism. Generic PromptTokenLimit and the prompt-tsx-style hierarchical renderer remain available to other callers and old persisted data; neither becomes a visual QoS scheduler. The Builder commits provisional IDs only after rendering the exact text returned to the caller. Chat stores and forwards that text without rerunning visual allocation. Existing attachment MessagePack key 2 remains compatible by wrapping final text in PromptText.

## Allocation Policy

Preserve current structural admission and relevance order initially. Allocate remaining estimated tokens progressively across roots, then across admitted bodies within each root. Every body has an equal base share; relevance breaks integer-share ties rather than multiplying the existing offscreen penalty again. Short bodies release unused demand to other bodies. Composite previews compete as one body, not once per underlying member. One root removes only the outer competition, not fairness among its bodies.

For divisible in-memory text prefixes, capped progressive filling is the initial QoS implementation. Full packet-cost DRR with accumulated deficits remains an alternative if indivisible work is introduced; do not add packet queues merely to split already bounded strings. Neither method promises a globally optimal semantic summary.

Candidates and final prefixes use the same estimator as Prompting, including escaped text. Whole-output validation accounts for markup, names, IDs, status, and continuation. Correct estimation overshoot by decreasing the allocation budget monotonically and recomputing shares, never by silently deleting one body's entire allocation. The final output must fit the configured estimator budget, not every model's tokenizer.

Every body is a continuous prefix. Safe UTF-16 cuts are allowed even in a long line without word boundaries. Planned short previews use moreText without failure status. Structural budget omissions retain explicit status. An ancestor is not excluded by role; unused capacity can legitimately go to one long Document. Remove the covered-ancestor experiment rather than layering its restoration dependencies onto allocation.

## Separate Boundaries

Snapshot's existing per-node and total character caps bound observed candidates. Final strings avoid persisting a deferred tree carrying hidden full previews. Missing Snapshot text cannot be recovered by allocation and must not trigger new platform reads in the Builder. If skeletons consume all budget, body fairness cannot manufacture space; changing structure/content admission is a separate evidence-gated decision.

read_visual_text finalizes its page and next offset together. Chat must not implicitly truncate a page while retaining its original continuation. Native IDs, retention, turns, and action semantics do not change.

## Verification

Compare fixed snapshots for long/short competing bodies, all-long bodies, one remaining long body, multiple roots, Composite bodies, huge single lines, and structure-only budget pressure. Check body coverage, prefix integrity, final estimator cost, published-ID equivalence, determinism, and build cost. Real-web smoke tests check navigation and ID/text continuation, not that a Document must become shorter. Record no claimed A/B improvement without fixed-input evidence.

## Initial Implementation Evidence

The implementation uses capped progressive filling, with generic TokenLimit only for skeleton admission. After body allocation it renders without pruning and validates the final string before committing targets. Structural query, text read, and window-list tools return strings; the debugger and WebView Probe use the same boundaries. Attachments store PromptText at the unchanged MessagePack key.

139 Automation tests and seven focused Core tests passed, covering long-body competition, short-body demand release, Root and Composite allocation, UTF-16/escaping, budget compliance, repeatable output, retained lookup, and existing query/turn/serialization behavior. This replaces the ancestor-dedup tests rather than keeping both algorithms.

The native WebView MCP regression is recorded locally under `tests/Everywhere.Automation.WebView.Probe/artifacts/final-text-qos-20260907`. Example Domain passed temporary/persistent turn and historical-transition checks. MDN, GitHub, and Wikipedia initial queries represented 112, 113, and 117 targets respectively; their returned child IDs were usable. MDN reached the observed text end in three pages; GitHub and Wikipedia returned four consecutive pages with continuation and no page-local failure status. These changing-site runs are smoke evidence, not controlled old/new comparisons. Their output lengths alone do not measure retrieval quality.

Remaining evaluation: same-Snapshot old/new cost and coverage comparison, followed by task-based extra-read counts. Structure-dominated budgets and Snapshot-stage text starvation remain separate evidence-gated work, not guarantees of this allocator.

## Follow-Up Classification and Commit Boundary

The bounded Windows retrieval chain, conversation-turn retention, independent text paging, final-string output, progressive allocation, and supporting probes form one coherent implementation baseline. The following work does not block committing that baseline:

- **Evaluation:** fixed-Snapshot old/new coverage and build-cost comparisons, followed by task-based total output and extra-read counts. Also measure the existing offscreen multiplier separately if needed. Current reachability and property tests do not establish a general retrieval-quality improvement.
- **Optional utilization improvement:** redistribute small unused prefix allowances only if measurements show meaningful waste. The budget is a ceiling, not a requirement to fill every remaining token.
- **Evidence-gated admission changes:** distinguish skeletons consuming the output budget from Snapshot failing to collect later bodies. Changing structural admission affects discoverability; changing native text collection may add RPCs and transfer costs. Neither is part of the current body allocator.
- **Independent implementation gap:** provider/PID-local failure suppression remains unfinished. Current aggregate failure accounting bounds traversal but can stop unrelated roots too. Complete attribution and local suppression separately; Screen and other non-provider elements must not be assigned artificial PID semantics.
- **Optional structural compression:** broader Composite regions are not required merely because earlier specifications listed them. Revisit only for measured structural overhead, with retained-member lookup and independent interactive targets preserved.
- **Separate workstreams:** native macOS/Linux migration, Computer Use/input guards, and prototype visual QA retain their own platform and interaction boundaries.

Recommended order after this commit: retain a stable comparison baseline, gather effectiveness evidence, and separately address provider-local failure isolation. More complex allocation or compression is not an automatic next milestone.
