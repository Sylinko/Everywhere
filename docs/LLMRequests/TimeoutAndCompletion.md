# Request Timeouts and Completion Evidence

Status: design decisions and source review updated on 2026-10-03. The stage-one SDK repairs are implemented; the stage-two classifier is implemented and a common idle timeout is not. See [ExceptionNormalization.md](ExceptionNormalization.md) for the classifier boundary. Preserve working third-party API compatibility while normalizing the evidence that is actually available.

## Timeout contract

Do not impose a total deadline on an entire generated response or conversation turn. A long generation that continues receiving data may run for as long as needed.

| Phase | Boundary | Configuration |
| --- | --- | --- |
| Response wait | HTTP send through receipt of the final response headers, including connection establishment, request upload and server waiting | Preserve existing RequestTimeoutSeconds values and its current default of 20 seconds. |
| Response-body read | A pending asynchronous read of the response body, including the first read after headers | A separate configurable idle limit, with a way to disable it. The property name, default and serialized disable value are not finalized. |

The earlier suggestion of a 120-second idle default was not a confirmed decision. Codex and DeepSeek use 300 seconds in the inspected paths, but those values are reference points rather than a universal requirement. Introducing a finite limit where an SDK previously waited indefinitely is a behavior change and needs an explicit default/migration decision.

For the common Everywhere implementation, prefer observing raw response-body reads. Any returned body data ends that read's wait, including SSE comments, heartbeats, usage and status updates. There is no separate deadline for producing user-visible text. A server that keeps sending heartbeats may therefore remain active without producing an answer; user cancellation remains available.

Arm the idle timer while a read is pending and stop it when the read settles. Parsing, UI processing, consumer backpressure, tool execution and retry backoff do not count as network-read idle time. Each new HTTP attempt has its own response-wait lifecycle. Apply the read mechanism to error bodies as well as successful SSE/NDJSON responses.

## Ownership and implementation boundary

All six current SDK paths receive Everywhere's HttpClient through ModelConnection. A connection-owned handler/content wrapper can return a stream that forwards reads with linked caller/timeout cancellation. Keep this behavior scoped to LLM connections, preserving the existing proxy, authentication and HttpClient ownership rules; do not change unrelated application HTTP traffic.

Use CancellationTokenSource.CancelAfter around the actual asynchronous read, then disarm the timer in finally. The runtime manages its timer queue; application code does not need a thread per request or native timer APIs. Setting NetworkStream.ReadTimeout alone does not cover ReadAsync. A timeout must terminate the real read and release the response before replay; merely timing out an await and abandoning the underlying operation is insufficient.

The timeout owner records which phase expired. Exception normalization consumes that request-scoped evidence rather than inferring user cancellation from every OperationCanceledException or from a token canceled after an unrelated error. If an SDK timeout's phase is not available, retain that uncertainty instead of inventing a phase. Keep the evidence out of shared last-response fields.

Coordinate existing SDK timers when enabling the common implementation:

| Path | Existing extension point and required coordination |
| --- | --- |
| OpenAI Chat / Responses | Public client options expose NetworkTimeout. System.ClientModel currently adds a 100-second read-timeout wrapper. Disable that timer when the application owns both phases, or otherwise align it explicitly; it must not silently override a longer or disabled application idle limit. Setting NetworkTimeout alone cannot independently represent two different phase limits. |
| Anthropic | Public ClientOptions.Timeout and the supplied HttpClient control the send path. In the pinned implementation, ExecuteOnce's deadline sources are disposed after sending, before subsequent streaming reads. Align that send deadline with the response-wait owner; the supplied response stream can provide the idle limit. |
| Google / Mistral / Ollama | Use the supplied HttpClient and common response stream. The first-stage patches already make relevant reads cancellable and preserve failed-response ownership. |

These extension points are sufficient for the proposed timeout mechanism; no further SDK patch is currently required. Production wiring and real-HTTP tests must verify this end to end before enabling it. Only a demonstrated SDK bypass, discarded evidence or uncancellable operation would justify another targeted patch.

References: [HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0), [CancelAfter](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelafter?view=net-10.0), [NetworkStream.ReadTimeout](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.networkstream.readtimeout?view=net-10.0), and the pinned System.ClientModel ReadTimeoutStream reviewed in [SDKReview.md](SDKReview.md).

## Completion evidence and compatibility

Treat completion evidence separately from transport success and business validity. Preserve explicit provider reasons and their origin; do not fabricate a normal stop when the SDK only reports normal enumeration exhaustion.

| Observed evidence | Interpretation |
| --- | --- |
| Explicit normal stop or tool-call completion | Preserve the reason and let the caller validate its result. |
| Explicit output limit | Raise GenerationLimitExceeded, preserve useful output with an error, and stop without replay. |
| Explicit filtering, refusal or provider failure | Raise the classified failure. Missing diagnostic detail does not turn an explicit failure event into success. |
| Actual transport exception or locally identified timeout | Normalize the failure using transport and request-context evidence. |
| Normal enumeration end without a known finish field or observable terminal marker | Record missing/unknown completion evidence and retain current compatible-provider behavior. Absence alone does not trigger rejection or replay. |
| Unrecognized finish reason | Retain the raw value for diagnosis. Do not add a whitelist that rejects an otherwise usable compatible response solely for a new value. |

SDK parsers can consume terminal markers internally. A marker not exposed to Everywhere is not evidence that the server omitted it. Do not add raw SSE parsing, SDK patches or provider capability switches solely to demand completion fields that existing compatible services omit.

Use the first-stage finish-reason/usage repairs, existing SDK content and available raw representations as normalization inputs. Tool arguments and business results still follow the existing validation path. Explicit unsuccessful termination is handled centrally through HandledChatException, not an optional consumer flag. Compression still validates summary usability. Successful execution of submit_approval ends review immediately; later provider output does not reverse that decision. No blanket requirement for a finish field is introduced. Incomplete tool fragments must not become executable calls, but this stage does not introduce additional protocol-compliance checks over otherwise accepted results.

## Reference implementations

These observations describe the local source snapshots inspected on 2026-10-03, not every release or deployment of either product.

| Repository / snapshot | Relevant implementation | Observation |
| --- | --- | --- |
| Codex ca466061d6 | codex-rs/model-provider-info/src/lib.rs; codex-rs/codex-api/src/sse/responses.rs | Default SSE idle timeout is 300 seconds. timeout wraps the next parsed SSE event, rather than each raw byte read. Normal EOF before a recognized terminal response is an error. |
| Codex ca466061d6 | codex-rs/codex-api/src/sse/responses.rs | response.completed produces Completed. response.incomplete with interrupted also completes with end_turn=false; content_filter has a distinct error; other incomplete reasons become stream errors. This Responses-specific policy is not an Everywhere-wide rule. |
| DeepSeek harness 639ed01539 | packages/util/timeout/src/index.ts; packages/llm/llm-deepseek/src/adapter.ts and sse.ts | A 300-second watchdog runs only while iterator.next is outstanding. Native Messages parsing pulses it on SSE events and heartbeat comments. The first outstanding iteration also covers request preparation/send; this is not two independent phase settings. |
| DeepSeek harness 639ed01539 | packages/llm/llm-deepseek/src/translate.ts; packages/llm/llm/src/assembler.ts | Native Messages requires message_stop, a recognized stop reason and closed blocks. max_tokens retains text/reasoning and drops the response's tool calls. These strict native-protocol rules are reference behavior, not a blanket compatibility requirement. |
| DeepSeek harness 639ed01539 | packages/llm/llm-pi-ai/src/adapter.ts, config.ts and catalog.ts | Third-party integration combines an iterator idle watchdog with optional SDK timeout settings. It exposes supportsFinishReason=false to allow pi-ai to infer termination for endpoints without that field. |

The shared lesson is to bound stalled waiting and retain explicit completion semantics. Their exact timeout placement and protocol checks differ. Everywhere should preserve its broader existing compatibility rather than transplant either implementation's full acceptance rules.

## Stage-two validation

Run wire fixtures through the real SDK and the production normalization entry point. Assert extracted evidence, classification/recovery advice and friendly-message separation; a standalone keyword-function test does not establish the full behavior.

When common timeout wiring is implemented, cover delayed headers, headers followed by no body, stalled error bodies, heartbeat-only activity, a generation whose total duration exceeds its idle limit, consumer pauses, caller cancellation and actual timeout-origin reporting. Verify response release and absence of overlapping abandoned reads. Keep SDK/application timer coordination in these tests, including the disabled-idle case.

For completion, exercise explicit stop, tool calls, output limit, filtering/provider errors, genuine broken HTTP bodies, missing finish fields and unknown reasons. Normal EOF with absent metadata must retain the agreed compatibility behavior, while the same partial content followed by an actual transport error remains a failure.
