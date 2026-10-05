# LLM Request Integration Test Architecture

Status: updated on 2026-10-03. The NUnit/MockServer harness, confirmed SDK repairs, exception normalization, and logical request executor are implemented. The full Release suite passes 388 tests through WSL Containers: 286 real-HTTP SDK/application cases and 102 local normalization cases, with zero skips. A separate focused Core run passes 84 tests. See [SDKReview.md](SDKReview.md#9-implemented-repairs-and-verification) for first-stage verification and the verification sections below for current coverage. The scenario matrix below includes planned coverage and is not a claim that every case is implemented.

## Purpose and boundary

Exercise the client path used by Everywhere against a controlled loopback HTTP server. Feed wire responses into real SDK parsers and the actual patched assemblies, then observe content, tool calls, completion metadata, exceptions, cancellation, physical request counts and request payloads. Do not manufacture the expected SDK exception in a fake completion service and call that a regression test of the SDK.

The same server and protocol fixtures now support production KernelMixin request-execution tests. First-stage tests establish transport and SDK behavior; application tests separately assert retry and lifecycle semantics through their owning production entry points.

## Data reference, not framework migration

The inspected mockllm checkout is commit `4d4b1942ef8dc22c18f495f280109df1d621fdf6`. Relevant upstream sources are [answer.rs](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/answer.rs), [core.rs](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/core.rs), and [transport.rs](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/transport.rs).

Use mockllm primarily as a source of representative error payloads, protocol event sequences and observable fault scenarios. Do not translate its Provider/Endpoint/Session/Answer abstractions or reproduce its server framework. Its cut_after behavior is a useful scenario distinction: a body error after emitted frames differs from a normally terminated HTTP body missing the protocol's completion event.

Keep the existing NUnit runner/assertions. MockServer 8.0.0 is the selected host; its codecs generate normal responses and its REST control plane serves raw error/fault fixtures. No Rust build or installed mockllm service is required. HTTP parsing, listening and response transport belong to the server implementation. Repository code supplies fixture data and narrow test orchestration, not an extensible scenario language, provider emulator or a handwritten HTTP server.

Do not port mockllm's complete endpoint catalog or per-model validation tables. Cover Everywhere's current streaming paths: OpenAI Chat Completions and Responses, Anthropic Messages, Gemini, Mistral and Ollama chat. Mistral/Ollama fixtures need their own source attribution; the inspected mockllm provider modules cover OpenAI, Anthropic and Gemini. Record fixture provenance and label deliberately malformed or gateway-style responses as synthetic.

## C# tooling decision

The repository already pins NUnit 4.6.1, Microsoft.NET.Test.Sdk 18.5.1 and NSubstitute 6.2.0. Keep the established runner and assertions; NSubstitute is not a replacement for the HTTP boundary under test.

| Candidate | Suitability and decision |
| --- | --- |
| NUnit + MockServer | Selected after the pinned-release capability probe. Built-in codecs cover normal text/tools; raw HTTP responses cover missing codec fields, error bodies, stalled reads and transport interruption. Requires an external server process/container. |
| NUnit + Kestrel | Previous initial recommendation, now a fallback for concrete gaps established by the MockServer probe. Run real loopback HTTP and use normal asynchronous response writes, gates and HttpContext.Abort for the required stream faults. A test-only Microsoft.AspNetCore.App framework reference must not become an application runtime requirement. |
| WireMock.Net | Mature option for status/body/header mappings, request matching/recording and stateful response sequences. Current documentation describes limited built-in response faults; the documentation reviewed does not establish the precise gated partial-stream behavior needed here. Do not assert it is impossible, but do not add it alongside Kestrel merely to serve small static fixture files. Reconsider if request matching requirements materially outgrow the small host. |
| ASP.NET Core TestServer | Useful for middleware tests, but its in-memory transport does not establish real socket failure and HttpClient behavior. Not the primary transport for these SDK regressions. |
| Mock HttpMessageHandler / fabricated SDK exceptions | Suitable for isolated later policy tests, not evidence that the SDK cancellation, parsing or transport patch works. |

Official references checked on 2026-10-02: [WireMock.Net stubbing](https://wiremock.org/dotnet/stubbing/), [scenarios](https://wiremock.org/dotnet/scenarios-and-states/), [documented faults](https://wiremock.org/dotnet/faults/), [ASP.NET Core HttpContext and abort](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/use-http-context?view=aspnetcore-10.0), [Kestrel endpoints](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0), and [TestServer limitations](https://learn.microsoft.com/en-us/aspnet/core/test/middleware?view=aspnetcore-10.0#testserver-limitations). Context7 was also consulted for WireMock.Net. Runtime evidence for the selected host is recorded below.

### MockServer evaluation

The user identified [mock-server/mockserver-monorepo](https://github.com/mock-server/mockserver-monorepo). Current [LLM documentation](https://www.mock-server.com/mock_server/llm_response_mocking.html) describes deterministic httpLlmResponse generation for OpenAI Chat/Responses, Anthropic, Gemini and Ollama, plus Mistral as an OpenAI-compatible codec alias. It includes text, tool calls, stop reasons, usage, SSE/NDJSON framing, timing controls, error statuses/Retry-After, truncation and malformed events. This is directly relevant mock functionality, separate from optional features that call an actual LLM.

The [C# client README](https://github.com/mock-server/mockserver-monorepo/tree/master/mockserver-client-dotnet) exposes typed LlmMockBuilder APIs; parts of the website still describe non-Java clients using raw expectation JSON. Validate the selected released server/client pair rather than assuming every master feature exists in the installed package. Plain HttpClient against the REST control plane remains an available fallback without building a second client library.

[Recording documentation](https://www.mock-server.com/mock_server/ai_traffic_inspection.html) describes turning captured SSE traffic into replay expectations. Treat this as event replay, not a faithful recreation of original timing, byte boundaries or socket faults. Verify the chosen version's redaction and capture-limit behavior; captured payloads still require review before entering the corpus. Preserve raw pathological fixtures so a normal-response generator cannot erase the bug under test.

The server runs separately from .NET: Docker or a Java runtime/JAR, with self-contained runtime bundles also documented in the repository's packaging sources. Test dependencies and process startup/teardown need an explicit choice; do not install Docker/Java or add a mandatory service as part of this design update.

Before committing to a host, run a bounded capability probe against a pinned release:

1. All six actual SDK client paths accept normal text and tool-call streams, including Ollama NDJSON and Mistral-specific extensions where needed.
2. Script 429/503 followed by success and verify physical requests and response evidence.
3. Emit content, hold the response open, cancel the client, and establish that the blocked read and response are released.
4. Separately test MID_STREAM truncation, a clean HTTP end without protocol completion, and an actual interrupted HTTP body. Documentation describing truncation alone does not establish TCP/HTTP termination semantics.
5. Import/replay one sanitized captured-style SSE fixture without changing event content; verify custom error bodies can bypass normalized provider generation.

The runtime probe selected MockServer without a custom Kestrel host. Its MID_STREAM behavior completes HTTP framing normally; tests use raw responseBytes to exercise pending body reads and premature HTTP EOF separately. Its Responses tool codec omits call_id: the normal baseline uses a valid SSE fixture, while compatibility regressions deliberately exercise the raw codec and patched MEAI adapter. Its Chat streaming codec omits usage, and its Ollama codec omits done_reason; independently written wire fixtures cover those fields rather than treating generator omissions as SDK defects. Corpus estimates below remain planning estimates; actual error data currently comprises 35 entries (14 source-backed, 21 synthetic).

### Optional integration tests and container runtimes

Docker is not a required dependency of the test project. Tests consume a MockServer base URI through `EVERYWHERE_MOCKSERVER_URL`; the server can be started with WSL Containers, Docker, a Java/JAR process, or an existing dedicated test service. Keep server provisioning separate from SDK assertions.

Use the existing NUnit infrastructure with an `LlmIntegration` category. Category alone does not disable tests: a shared fixture setup explicitly skips with a clear reason when the URI is absent and the integration suite has not been required. `EVERYWHERE_REQUIRE_MOCKSERVER=1` makes missing configuration a setup failure. A supplied but unreachable endpoint, unsupported version/capability, failed image pull, or failed server startup is an error, never an environment-based skip. An explicit integration-run helper sets required mode before invoking `dotnet test --filter "TestCategory=LlmIntegration"`. Default test runs neither pull images nor start containers. Pure data/policy tests remain runnable without a server.

CI uses a dedicated integration job with required mode enabled, a pinned server version/image digest, and a check that the intended suite actually executed. Its runtime failures cannot produce a green build through skipped SDK tests. Real-provider probes remain a separate opt-in category and do not share activation with offline MockServer tests.

Provisioning supports a small, explicit choice: existing endpoint, `wslc`, Docker, or opt-in auto selection. On Windows, auto selection can prefer WSL Containers when usable, then Docker if available; other systems can select Docker. Merely finding the executable is insufficient. Once a runtime is selected and startup begins, report its failure rather than silently switching and hiding the problem. An existing endpoint takes precedence and does not require a container CLI. Manual JAR/bundle startup uses that same endpoint mode; no initial automatic Java installer/launcher is needed.

Keep runtime-specific operations limited to start, inspect published endpoint, read logs, and stop/remove the container created by this run. Use unique owned container identities, bounded readiness checks and cleanup, and do not reset an entire user-supplied MockServer instance. Isolate expectations and recordings by supported namespaces/IDs or require a dedicated server where isolation cannot be guaranteed. Do not assume Docker API compatibility or identical CLI/output formats across runtimes. Avoid introducing a general container orchestration abstraction.

Local runs on 2026-10-02/03 verified WSL Containers 3.0.1.0: detached MockServer startup, random loopback ports discovered through inspect, Windows HTTP connectivity, and cleanup of the run's owned container. WSLc puts Ports at the inspect result root; Docker puts it under NetworkSettings. The runner handles those two observed formats explicitly. Docker was not installed locally; its CI workflow remains unexecuted.

Microsoft's [WSL container overview](https://github.com/MicrosoftDocs/WSL/blob/main/WSL/wsl-container.md) documents running Linux containers and accessing published ports from Windows, and offers `Microsoft.WSL.Containers` for direct C# integration. The implementation uses the installed CLI without that additional package. NUnit supports [category filtering](https://docs.nunit.org/articles/nunit/writing-tests/attributes/category.html). The runner preserves a console log and TRX, captures its owned container's logs on failure, and removes only that container. Existing endpoints use per-test scoped expectations and recordings. No real-provider requests are made.

## Corpus size and selection

Measured against the pinned mockllm checkout, counting test attributes rather than individual assertions:

| Inspected file | Tests | Source lines |
| --- | ---: | ---: |
| openai_chat.rs | 30 | 692 |
| openai_responses.rs | 14 | 483 |
| anthropic_messages.rs | 26 | 716 |
| gemini_generate.rs | 11 | 278 |
| transports.rs | 3 | 177 |
| Total | 84 | 2,346 |

These files total 80,751 bytes; this is reference source size, not a proposed import. The four protocol files alone contain 81 tests, many for model-specific parameter validation outside our scope. Existing ErrorClassification.md contains 14 source-backed error examples and 20 synthetic/behavioral cases. Of the latter, 14 are message/payload cases and six describe runtime/wrapper/completion behavior; they should not all become static JSON files.

Estimated first-stage deliverables, subject to deduplication and actual SDK behavior:

| Item | Estimate | Counting rule |
| --- | ---: | --- |
| Error payload cases | 32-40 | Start from the 28 source/synthetic message examples; add only missing provider-specific or malformed-body cases. Do not clone the same payload for every status. |
| Valid stream fixtures | 12-18 | Text and tool-call baselines for six client paths, with selected reasoning/usage/completion variants. |
| Transport/lifecycle scenarios | 10-14 | C# actions/gates reusing those fixtures, not another set of copied transcripts. |
| Parameterized SDK test executions | 80-120 | Select combinations by shared transport, distinct parser and affected patch; not the full protocol x payload x fault Cartesian product. |
| Checked-in fixture data | Approximately 50-200 KiB | Small JSON/SSE/NDJSON examples plus provenance; an estimate, not measured generated output or a required quota. |

Start with a vertical slice of approximately 15-25 executions covering Google/Mistral transport and error-body cancellation, Ollama chat cancellation/error objects, and their happy paths. Then extend to all six paths. Numbers are planning estimates, not coverage targets; every case must distinguish a concrete behavior or regression. Shared OpenAI error payloads can exercise both Chat and Responses without duplicate files.

Store wire data as readable JSON, SSE or NDJSON with a small typed manifest containing case ID, protocol/applicable clients, HTTP status/headers, payload file, provenance (repository commit/path or official source), and whether the case is synthetic. Put scheduling/gates and semantic assertions in C# tests. Preserve independent expected content rather than generating the fixture and expected SDK result with the same mapping code. Record attribution/license requirements when extracting upstream material. Do not embed service credentials, real user transcripts or large binary media.

Initially use one dedicated SDK test project with fixture and server-helper folders; extract a separate shared test-support project when KernelMixin/ChatService tests actually need it. Avoid creating separate public libraries for hypothetical consumers.

## Adding observations from real APIs

Leave the fixture format open to collaboratively collected API observations. Distinguish provenance as reference-derived, synthetic, or observed; an observed response is evidence about a particular service/version/date, not a universal protocol requirement. Keep wire protocol and service identity separate so third-party OpenAI-compatible providers can contribute different cases without new server abstractions.

The small manifest may optionally record service label, observation date, reported model identifier, SDK version, a sanitized request file, and explanatory notes. Preserve the response status, relevant headers and original event ordering in the existing payload files. Use stable case IDs; new observations normally add files and parameterized test data, not another provider implementation. Evolve metadata only when a real sample needs it.

The intended workflow is: make an explicitly requested real API probe with a minimal test prompt; collect the response before SDK parsing where available; remove credentials, identifying URLs and private content; minimize the sample without changing the failing behavior; then replay it through the real SDK against loopback. Review the expected assertion separately from the captured result. Retain field presence, nulls, error wording and call-ID relationships when those are relevant to the regression. A changed or minimized sample is labeled as derived from an observation, not an untouched capture.

Record relevant timing or disconnection observations as notes first. An HTTP transcript does not preserve TCP packet boundaries or prove the cause of a connection failure. Translate a reproduced timing condition into an explicit gate/fault action; do not replay production wall-clock delays as fragile test expectations. Keep alternative valid provider behaviors as separate cases instead of normalizing away the difference.

Routine tests and CI remain offline and require no real credentials. Live probes are separately opt-in, with credentials supplied locally rather than saved in fixtures. First-stage work only needs the data/import convention; a recorder, live-test runner or automatic capture pipeline can be added later when actual collection work demonstrates the need. No real API calls are authorized or required by this design update.

## Explicit exclusions and completion boundary

First-stage scope is HTTP/1.1 loopback streaming generation through the six listed client paths. Exclude WebSockets/Realtime, images/audio generation, embeddings, file/batch APIs, complete model capability tables, API business validation, proxy/TLS/DNS emulation, HTTP/2/3 conformance, load benchmarks, a standalone mock CLI/admin UI, production service access, and automatic corpus synchronization. Add a missing transport or endpoint only for an actual Everywhere caller or demonstrated regression.

First-stage scope fixes SDK defects and records supported behavior independently of application policy. Subsequent implemented stages add exception normalization and the request executor described in [RequestExecution.md](RequestExecution.md), including production SDK retry suppression. The common read-idle timeout remains undecided. Normal protocol variation is characterized before deciding whether it is a defect: for example, a missing [DONE] marker with a valid finish reason is not automatically equivalent to an unfinished response.

Complete the stage when the known defects are reproduced then repaired through real SDK entry points, the affected normal paths remain valid, the planned evidence survives to its boundary, all fixtures have provenance, and the suite/publish checks run without an external service. Classify each remaining behavior as repaired, intentionally characterized, or unresolved with a concrete reason; do not declare completeness solely from a test-count target.

## Responsibilities

| Component | Responsibility |
| --- | --- |
| Loopback server | Own an ephemeral loopback port, script requests, stream/flush bytes, interrupt or suspend responses, record requests, and dispose all outstanding connections/tasks. |
| Response script | Describe status/headers and ordered body operations. Allow normal completion, protocol errors, missing terminal events, transport interruption and synchronization gates. |
| Protocol fixtures | Produce valid normal text, reasoning, tool-call fragments, usage and completion events, as well as source-backed errors. Keep semantic expectations separate from serializers so tests do not merely repeat the same mapping code. |
| SDK tests | Construct the real SDK/connector configuration used by Everywhere, redirected to loopback. Exercise public streaming entry points and the patched runtime binaries. |
| Later application tests | Reuse scripts and request recordings through KernelMixin and ChatService; add application retry, rollback and tool-result invariants without inventing a second server. |

Prefer explicit gates such as request received, headers flushed and first event consumed over short sleeps. Tests cancel only after establishing the relevant phase. Bound every test and teardown so a failed cancellation repair cannot hang CI. Timing assertions should distinguish the intended phase and permit scheduler variation rather than require exact millisecond delays.

Scripts are consumed deterministically per endpoint/scenario. An unexpected extra request must be recorded and fail visibly; do not return an unscripted success that hides an accidental retry. Record request method, path, relevant headers and bounded body, with scenario/request identifiers for concurrent tests. Use dummy credentials only.

## First-stage scenario matrix

| Area | Real HTTP scenario | Required observation |
| --- | --- | --- |
| Normal operation | Valid text, reasoning where supported, fragmented tool arguments, parallel calls where supported, usage and terminal event | Actual SDK output retains the supported content and completion metadata; patches do not damage the successful path. |
| Error evidence | Source-backed 400/401/403/404/413/429/5xx bodies, Retry-After and request ID | Preserve actual status/body and required headers at the agreed boundary. Compare exception shapes without treating protocol identity as service identity. |
| Gateway compatibility | Generic 503 plus specific permanent-error message; malformed JSON, empty body and HTML error page | Preserve available evidence without inventing an empty-response or parameter diagnosis. Classification expectations belong to the subsequent classifier stage. |
| Transport failure | Connection accepted and closed before response headers | Google/Mistral do not invent HTTP 400. Observe the real transport exception rather than constructing one in the test. |
| Error-body cancellation | Send error headers and a partial body, then hold the response open | Cancel the client after the body is pending; cancellation propagates and the response is released. |
| Chat-read cancellation | Send a valid first event, then stall the next event or leave a partial line | Ollama's actual IChatClient streaming path cancels its pending read, without being mistaken for successful completion. |
| Protocol error | HTTP 200 followed by the protocol's error event/object, before or after text | The client surfaces the error and retains its diagnostic content; Ollama does not map it to empty text. |
| Broken transport | Emit part of a response and abort the body/connection | Observe SDK behavior and exception evidence; there is no overlapping abandoned read. |
| Incomplete protocol | Complete HTTP framing normally but omit terminal metadata/events | Record actual SDK behavior separately from transport interruption. Preserve available explicit incomplete/error evidence, but do not reject or retry solely because a compatible response lacks finish metadata or an exposed terminal marker. |
| Framing | Split JSON, SSE delimiters and a multibyte UTF-8 character across body writes | Valid streams still parse; event boundaries are not assumed to equal network-read boundaries. |
| Timeouts | Hold headers, then separately hold a post-header read | Characterize current SDK timeout ownership. Do not silently introduce the future application timeout contract. |
| Built-in retry | Script retryable failures followed by success/exhaustion | Record current physical send counts, and verify supported zero-retry configuration independently. Production defaults are disabled only when the application executor replaces them. |
| Concurrency | Interleave two requests with distinct errors and headers | Diagnostic evidence remains attached to its originating request. |
| Disposal | Cancel or dispose enumeration early | Outstanding operations terminate and responses/connections are released; server teardown is not used to make a cancellation test pass. |

Not every provider supports every semantic event. Make applicability explicit rather than skip unexplained failures or assert identical exception types across all SDKs. OS-specific DNS/TLS failures need dedicated controlled probes if later required; a loopback HTTP server alone does not validate them.

Implemented coverage includes normal text/tools, usage and explicit finish reasons,
the 35-entry error corpus, gateway bodies, pre-header disconnects, protocol errors,
missing terminal events, broken bodies, pending-read cancellation, early stream
disposal, selected-header retention, concurrency, and existing SDK retry counts
and failure/failure/success recovery. Each assertion records its applicable paths;
the 163-case total is not a provider-by-scenario Cartesian product.

Deliberately split UTF-8/event framing and separate header/read timeout probes still
need targeted cases. Stage-two normalization tests are implemented through the
production entry point; see [ExceptionNormalization.md](ExceptionNormalization.md).
The common timeout contract is specified in [TimeoutAndCompletion.md](TimeoutAndCompletion.md). Application
retry scheduling and consumer recovery are described in [RequestExecution.md](RequestExecution.md). Sanitized real-provider
observations can extend the existing fixture corpus without replacing the host or SDK entry points.

## Repair sequence and evidence

1. Build the server lifecycle and protocol happy paths first, proving that real SDKs accept the fixtures.
2. Add a defect scenario and run it against the unmodified dependency behavior. Record the observed failure and exact dependency version; do not leave a permanently ignored regression test.
3. Apply the narrow source or IL patch and rerun the same scenario, plus related normal/cancellation/disposal cases.
4. Repeat per defect, extending the common harness only when the next scenario needs it. Keep the research clones unmodified and build patches from repository-owned files.
5. Verify tests load the intended patched assembly and that publish replacement preserves it. A successful weave/build alone is insufficient.
6. Document remaining SDK differences, their fixtures and any unresolved behavior before starting the application executor.

Request-execution tests reuse this foundation for failure/failure/success retries, bounded diagnostics, cancellation during backoff, and unchanged request history. Headless ChatService tests cover provisional output rollback and retained earlier tool results. Native presentation interactions and full tool-driven subagent generation through compression still require application-level verification. The approval reviewer consumes the shared streaming request reader.

## Exception normalization verification

Before request execution was integrated, the 2026-10-03 Release run passed all 348 AI tests: the original 163 SDK boundary
cases, 83 real-HTTP production classification cases, and 102 local normalization/
observed-message cases (including 76 sanitized Sentry samples). Sentry records
are issue-message examples without occurrence counts or original SDK/HTTP evidence.
They are not counted as real HTTP tests.

Classification tests reference Everywhere.Core rather than linking a copy of the
classifier. The runner enables central package versions for the production graph,
and CI initializes ShadUI/Motion source submodules alongside Semantic Kernel.
No new NuGet dependency or mock framework is introduced.

## Request execution verification

The final 2026-10-03 Release run passed all 388 AI tests with zero skips. The 40
additional real-HTTP cases instantiate all six production KernelMixin implementations
and verify transient recovery, retry exhaustion, disabled retries, cancellation during
backoff, early consumer disposal, unchanged request bodies, context-overflow recovery,
and explicit output-limit completion where supported. Recorded physical sends confirm
that production SDK retry defaults do not multiply the application budget.

A separate focused Core run passed 84 tests across request recovery, presentation,
compression, assistant configuration, function-call assembly, statistics, and attachment serialization. Request-recovery cases
exercise the actual visible response consumer, retained earlier tool results,
per-attempt statistics, stopped-message persistence, parent/child cancellation,
consumer failure propagation, retry-row identity, output-cache pruning, and bounded
diagnostic formatting. A compiled AXAML template case verifies worker-originated source
updates to activity headers, previews, expansion and liveness without row forwarding
notifications. They do not substitute for a full application tool/subagent
scenario or native UI interaction.

Windows Release build completed with zero errors and eight Watchdog AOT/trim warnings.
All 13 localization files contain the eight new request-settings/status/detail keys.
The owned WSL Containers test server was removed after the run.

The retained request-recovery tests cover mutable activity identity, late worker
disposal, explicit compression ownership, effective-output timing, SDK Activity
parentage and per-attempt accounting. Footer aggregation, recursive usage ownership,
usage backfill and persistent turn/storage changes are outside this batch.

The verification totals above describe earlier runs. After separating this batch from
later development, build checks are performed again; application E2E verification is
run manually by the maintainer before committing.
