# Exception Normalization and Recovery

Status: rewritten on 2026-10-05. This working-tree architecture replaces the flat category enum and classification record. Earlier verification counts describe the preceding implementation and do not validate this rewrite.

## Ownership and dependencies

`ModelConnection`, `ChatExceptionEvidence`, SDK adaptation and request execution live in Everywhere.Core. They are implementation contracts rather than Abstractions interfaces. Connection-owned evidence enrichment remains independent of wire protocol identity; an API-compatible gateway is not implicitly the SDK vendor.

`ChatExceptionNormalizer.Handle` is the normalization entry point. It preserves already-handled exceptions and normalizes aggregate causes independently. The original cause remains in `InnerException`. Normalization does not execute requests or schedule retries.

1. Common extraction reads bounded HTTP/transport evidence and cancellation/timeout provenance supplied by the request owner.
2. `KernelMixin.ExtractExceptionEvidence` adds typed SDK response fields; the connection adapter interprets its own envelope.
3. Known CLR failures map directly to exception subtypes. `KernelMixin.NormalizeKnownException` can recognize concrete SDK failures without encoding a local-cause identifier. Unknown SDK causes fall through to response classification.
4. HTTP status establishes a baseline. Recognized specific error codes take precedence; scoped message rules supply missing details or a diagnosis when codes are generic or unknown.
5. The concrete exception supplies recovery advice through its virtual `Recovery` property. The request executor owns retry scheduling.

`HandledChatExceptionType`, `ChatExceptionClassification` and `LocalCause` are removed. External service codes and rule identifiers remain strings because they are diagnostic/protocol values, not the application's category system.

## Exception hierarchy

The abstract `HandledChatException` provides the original cause, localized presentation, expected-error semantics and one optional diagnostics reference. Concrete categories own their default localization keys and recovery advice. The base `Recovery` returns Stop; only categories requiring another action override it. An explicit friendly key from the owning connection or caller takes precedence.

| Parent | Specific causes |
| --- | --- |
| InvalidConfiguration | InvalidEndpoint, ModelUnavailable, InvalidTemperature, InvalidTopP |
| AuthenticationFailure | InvalidApiKey, LoginRequired |
| PermissionDenied | RegionRestricted |
| UnsupportedCapability | Tools, Images |
| InvalidRequest | ContextLengthExceeded, InvalidThoughtSignature, InvalidReasoningContent |
| NetworkError | HostNotFound, ConnectionRefused, TlsError, ProxyTunnelRejected |
| InvalidResponse | EmptyResponse, MalformedJson, UnsupportedFormat |
| Canceled | ByCaller |

Timeout, ServiceUnavailable, RateLimited, QuotaExceeded, ContentBlocked and Unknown are additional concrete categories. Unknown is not an expected failure. An opaque cancellation is Canceled with stop advice; only established caller cancellation is ByCaller.

Nesting and inheritance agree. Parent patterns match their specific causes without another category property. Category files split the hierarchy by purpose. Existing localization keys are reused, including existing messages in all 13 languages. The previously unused EndpointNotReachable enum value has no dedicated class; concrete connection failures belong to NetworkError.

## Evidence and diagnostics

`ChatExceptionEvidence` retains the effective HTTP and outer gateway status, service code/type/parameter, extracted message, bounded response body, request ID, retry delay, transport cause, socket code and cancellation/timeout provenance. It does not re-encode recognized CLR exceptions as LocalCause values.

`ChatExceptionDiagnostics` groups that evidence with rule/source and assistant model ID. It carries no second final category or recovery action. The selected exception is the sole final classification; no candidate exception list is retained.

Response-body copies are bounded to 32,768 characters and cause traversal to 16 exceptions. Only recognized error-envelope fields are analyzed. Malformed/unknown JSON retains diagnostics without scanning echoed request fields as prose. SDK normalization reads already-buffered failure content and makes no additional request.

Patched headers use Everywhere.Http.*; OpenAI exposes raw response headers. Retry hints accept finite nonnegative seconds/milliseconds and HTTP dates. Invalid hints are ignored; the longest valid hint is retained. Original exception/response objects retain their original information.

## Selection and recovery

Known caller cancellation, timeout and transport causes precede HTTP/message inference. TLS and proxy rejection remain distinct from API authentication or invalid parameters. SDK status zero does not establish an empty response.

HTTP baselines and existing gateway compatibility heuristics are retained. Generic server_error/api_error/invalid_request_error/RESOURCE_EXHAUSTED provide a baseline when no usable HTTP baseline exists. Typed errors, specific codes and scoped phrases can refine it.

Classification selects one result directly: connection-owned codes, recognized specific code/type, scoped message rules, then the HTTP/generic baseline. Parameter error codes without a parameter name can use the message to supply the missing diagnosis. Recognized specific codes are not discarded because message text suggests another cause. No conflict detector or type-hierarchy comparison participates in selection.

Recovery is an abstract record with sealed nested Stop, Retry, RecoverContext and Canceled results. Stateless results and Retry without a server delay are shared instances. Retry carries an optional RetryAfter; retryable exception subtypes receive the extracted delay at construction and retain their result. Subtype overrides do not parse raw messages. TLS/proxy failures stop; ordinary transport failures, timeout, service unavailability and transient rate limits permit retry. ContextLengthExceeded returns to the caller's compression recovery. Only ByCaller produces caller cancellation.

## Logical request failure and consumers

`ChatRequestException` derives from HandledException and composes `FinalFailure`, the last ten attempt records and total failure count. It delegates friendly presentation and expected-error semantics to FinalFailure. It never copies final category/evidence fields or impersonates a specific cause through inheritance.

`ChatExceptionNormalizer.GetFinalFailure` recognizes only the concrete chat exception and this known request wrapper. Compression and approval use that final cause explicitly; arbitrary inner-exception traversal is not a business recovery mechanism.

The request executor reads the concrete failure's `Recovery` and obtains the server delay from `Retry.RetryAfter`. Evidence retains the original delay for diagnostics. Telemetry and the details view identify the concrete CLR category. Persistent messages retain localized error keys while exception objects remain runtime-only, so this hierarchy change requires no database migration.

## Generation termination

The request boundary classifies explicit termination evidence from SK metadata and available raw SDK objects. Diagnostics retain normalized and provider finish reasons separately. Known OpenAI SDK enum-rejection exceptions use their structured ActualValue; arbitrary exception text is not interpreted as a finish reason. No raw SSE parser or success whitelist is added.

| Evidence | Concrete failure and recovery |
| --- | --- |
| length / max_tokens / max_output_tokens / MAX_TOKENS | GenerationLimitExceeded; Stop |
| Anthropic model_context_window_exceeded | GenerationLimitExceeded.ContextWindowExceeded; Stop, not input-context compression |
| content_filter / refusal / safety and documented policy blocks | ContentBlocked; Stop |
| recitation / language | ContentBlocked with a reason-specific localized message; Stop |
| insufficient_system_resource | ServiceUnavailable; Retry |
| pause_turn | InvalidResponse.ContinuationRequired; Stop: continuation is not implemented |
| malformed function/response or unexpected/excessive tool calls | InvalidResponse; Stop |
| missing_thought_signature | InvalidRequest.InvalidThoughtSignature; Stop |
| no_image | InvalidResponse.EmptyResponse; Stop |
| Google other/image_other, aborted, or explicit failed/incomplete status without a recognized reason | InvalidResponse.Incomplete; Stop |
| Missing or unfamiliar reason alone | No failure; retain compatible behavior |

Unsuccessful termination follows AttemptFailed and ChatRequestException, preserving reported usage and partial output. The ordinary chat error row identifies the concrete failure; consumers cannot accidentally ignore an incomplete-output flag. Successful enumeration exhaustion closes successful accounting without a Completed update. Approval may finish earlier when submit_approval successfully executes; later stream events are outside that business operation.

## Verification boundary

Existing real-HTTP SDK, local normalization and sanitized Sentry cases are migrated to concrete type assertions. Fixture expectedType values name nested exception types; these test-data identifiers are not production classification codes. Parent assertions accept valid refinements.

The termination refactor passed 58 existing real-HTTP request/SDK boundary tests and 30 focused Core request-recovery, approval, and tool-assembly tests, with zero failures or skips. Existing mock payloads were reused; no new fixture data was introduced. Live-provider E2E and native error-dialog interactions were not run. No timeout mechanism, storage migration or UI layout change is part of this refactor.
