# LLM Request Error Classification

Status: proposed deterministic heuristics and reference corpus; not implemented. Companion: [recovery architecture](Architecture.md).

## 1. Evidence and limitations

Reviewed local `E:\Source\mockllm` at commit `4d4b1942ef8dc22c18f495f280109df1d621fdf6` on 2026-10-02, with no working-tree changes reported. Upstream: [lihe07/mockllm](https://github.com/lihe07/mockllm). This phase uses source examples only; it does not introduce a Rust dependency or test server.

OpenAI-compatible and Anthropic-compatible describe wire formats, not service identity or guaranteed error semantics. An SDK exception subtype can be derived only from HTTP status and must not be mistaken for independent evidence of the underlying cause. Gateways may wrap a specific error in HTTP 503 or return only a string message. Conversely, a generic message must not erase useful HTTP evidence.

mockllm supplies representative protocol fixtures, not proof that all services emit the same codes/messages. Its scripted errors select a default error shape/type from status; `Answer::message` replaces the message without changing that shape/type. Consequently its code and message can intentionally conflict. Separate exact source examples from synthetic compatibility cases below.

Source references at the inspected commit:

- [OpenAI-shaped errors](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/openai/mod.rs).
- [Anthropic-shaped errors](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/anthropic/mod.rs).
- [Gemini-shaped errors](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/gemini/mod.rs).
- [Scripted message override](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/core.rs), [answer controls](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/answer.rs).
- [OpenAI request validation](https://github.com/lihe07/mockllm/blob/4d4b1942ef8dc22c18f495f280109df1d621fdf6/src/openai/chat.rs).

## 2. Current failure modes

The current [HandledChatException parser](../../src/Everywhere.Core/Common/HandledException.cs) mixes extraction, classification, and parser-chain termination:

- The OpenAI `ClientResultException` parser checks message substrings before HTTP fallback. `Rate limit reached for requests.` matches `limit` and becomes QuotaExceeded; HTTP 429 is then skipped because classification already exists.
- `quota`, `limit`, `exceeded`, `usage`, or `organization` independently imply quota exhaustion. These words also occur in rate limits and unrelated limits.
- `key`, `token`, `credential`, `authentication`, or `permission` independently imply an invalid API key. Output-token parameters and resource permissions are distinct cases.
- `model` alone implies invalid configuration, so `The model is overloaded` can mask service overload on paths using text first.
- `signature`, `reasoning_content`, `image_url`, `temperature`, or `top_p` alone select specialized categories, even if the term is merely echoed or has another meaning.
- The Anthropic parser stops at 5xx/rate-limit exception subtypes, preventing more specific contradictory body evidence from being considered.
- `HttpOperationExceptionParser` can terminate the chain even when its text lookup returns no category, losing an available HTTP fallback.

Retain heuristics, but make evidence extraction, rule specificity, and conflict resolution explicit rather than relying on whichever parser returns first.

## 3. Proposed classification procedure

1. Identify cooperative caller cancellation and concrete local transport failures first. Caller cancellation stops recovery and produces the persisted stopped outcome described in [Architecture.md](Architecture.md), not an assistant error banner or a diagnostic added to the retry-failure list. Distinguish a request deadline using its actual token/deadline context. A canceled token alone must not erase an unrelated concurrent failure.
2. Extract bounded evidence without choosing a category: HTTP status, SDK type, structured error `code`/`type`/`status`/`param`, message, Retry-After and request ID when available. Keep original diagnostics separately.
3. Establish an HTTP/transport baseline, distinguishing actual response status from SDK-generated wrapper status. The current SK helper fabricates HTTP 400 for transport failures; remove that behavior in the maintained connector source replacement rather than retaining a dedicated classifier workaround. OpenAI's ClientResultException with status zero remains transport evidence, not automatically an empty response (see [SDKReview.md](SDKReview.md)). Inspect origin/inner evidence before treating a wrapper as a server parameter rejection. For real responses, examples are 401 authentication, 403 permission, 429 rate/resource limiting, 503 unavailable, 504 timeout. Do not classify every 5xx as retryable (for example, explicit unsupported functionality differs from temporary overload).
4. Match recognized specific codes and concrete message patterns. Broad codes such as `server_error`, `api_error`, `invalid_request_error`, and `RESOURCE_EXHAUSTED` are baseline evidence rather than precise diagnoses.
5. Resolve conflicts by specificity. A concrete permanent cause can refine or override a generic 503/500/429 baseline. A bare `invalid`, `token`, `model`, or `limit` cannot. Preserve competing evidence in diagnostics.
6. If two equally specific causes genuinely conflict, do not manufacture certainty. Retain the status-family/ambiguous result; conservatively stop when a credible permanent failure conflicts with a transient interpretation. This conflict rule must remain explicit and testable.
7. Apply retry policy to the resolved evidence. Do not infer retry eligibility from translated messages. Unknown text without status/transport evidence is not automatically transient; unknown text accompanying a normal retryable 503 can use the status baseline.

Classification evidence should include a matched rule identifier and source (code, message, status, transport, or fallback) for debugging. Qualitative specificity is sufficient; numerical confidence scores are not required.

Text extraction should inspect the error message, not arbitrary echoed request bodies, headers, stack traces, or the entire serialized exception. Normalize case and whitespace for matching, but preserve the original diagnostic text. Use bounded parsing/scanning; failure to parse JSON must still allow plain-text classification. Token/phrase boundaries and subject/predicate combinations are preferable to unconstrained substring matching. Avoid expensive generic regex chains. Recognized local-language patterns can be added from real evidence; do not assume error messages are always English.

## 4. Semantic rule families

These categories describe meaning and policy, not a commitment to new C# enum names.

| Family | Specific evidence examples | Insufficient evidence / exclusions | Recovery |
| --- | --- | --- | --- |
| Authentication | `invalid_api_key`, explicit invalid/expired API key or authentication credential | `token`, `key`, a parameter name, or resource permission alone | Stop; credentials must change |
| Permission | Explicit permission denied for a resource, HTTP 403 baseline | Do not automatically diagnose region or invalid credentials | Stop |
| Billing/quota exhaustion | `insufficient_quota`, `billing_error`, credit balance too low, insufficient balance | `quota` alone; tokens per minute; generic RESOURCE_EXHAUSTED | Stop |
| Rate limiting | `rate_limit_exceeded`, `rate_limit_error`, rate limit reached, requests/tokens per minute, too many requests | Permanent billing evidence can override; `limit` alone is not enough | Retry |
| Context overflow | `context_length_exceeded`, maximum context length exceeded, input token count exceeds context capacity | `max_tokens` parameter rejection or output completion limit alone | Caller compression recovery |
| Invalid request parameter | `unsupported_parameter`, explicit unsupported/invalid/out-of-range parameter | Mention of `temperature`, `top_p`, or `model` without rejection | Stop |
| Output budget rejection | Explicit max_tokens/max_completion_tokens exceeds permitted output setting | Do not call this authentication or assume compression will solve it | Stop; revise settings |
| Capability mismatch | Explicit images/tools unsupported for this request | `image_url` may instead identify malformed input | Stop |
| Thought compatibility | Explicit invalid/missing thinking signature or required reasoning_content in message history | Generic signature mismatch may be credential signing; a field mention is insufficient | Caller/user recovery |
| Service overload | `overloaded_error`, overloaded, temporarily unavailable | `model` in the same sentence does not make it configuration failure | Retry |
| Timeout / interruption | Transport evidence, explicit request deadline/timeout; validated protocol error | Caller cancellation; permanent certificate failure | Retry when transient |
| Region restriction | Explicit unsupported region/location restriction | `location` alone or a generic 403 | Stop |
| Unknown | No adequate evidence | Do not guess a precise cause just to show an action | Status baseline if usable; otherwise stop |

Rate and quota ambiguity is unavoidable. In particular, generic HTTP 429 / RESOURCE_EXHAUSTED does not establish depleted credit. Use generic limiting wording and HTTP-based retry eligibility unless more specific evidence resolves the cause. With `RequestMaxRetries = -1`, an unresolved limiting response can keep retrying until cancellation; record this limitation rather than secretly imposing a finite retry count.

## 5. Corpus from mockllm source

Rows below transcribe source fixtures; they are not captured production incidents or runtime test results. The same payload should have consistent classification when carried through compatible SDK exception wrappers.

| Origin / HTTP | Code or type | Message / identifying excerpt | Expected interpretation |
| --- | --- | --- | --- |
| OpenAI-shaped / 401 | invalid_api_key | Incorrect API key provided. | Authentication; stop |
| OpenAI-shaped / 404 | model_not_found | The model does not exist or you do not have access to it. | Resource/configuration or access failure; do not guess which alternative; stop |
| OpenAI-shaped / 429 | rate_limit_exceeded | Rate limit reached for requests. | Rate limiting; retry, not billing exhaustion |
| OpenAI-shaped / 503 | server_error | The engine is currently overloaded, please try again later. | Unavailable; retry |
| OpenAI-shaped validation / 400 | unsupported_parameter, param=max_tokens | Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead. | Invalid parameter; stop, not authentication |
| Anthropic-shaped / 402 | billing_error | Your credit balance is too low to access the Anthropic API. | Billing exhaustion; stop |
| Anthropic-shaped / 403 | permission_error | Your API key does not have permission to use the specified resource. | Permission; stop, not automatically invalid key |
| Anthropic-shaped / 413 | request_too_large | Request exceeds the maximum allowed number of bytes. | Payload too large; stop, not automatically context overflow |
| Anthropic-shaped / 429 | rate_limit_error | Number of request tokens has exceeded your per-minute rate limit. | Rate limiting; retry, not authentication or billing exhaustion |
| Anthropic-shaped / 504 | timeout_error | Request timed out. | Timeout; retry |
| Anthropic-shaped / 529 | overloaded_error | Overloaded | Unavailable; retry |
| Gemini-shaped / 429 | RESOURCE_EXHAUSTED | Resource has been exhausted (e.g. check quota). | Ambiguous resource/rate limiting; do not assert depleted balance |
| Gemini-shaped / 503 | UNAVAILABLE | The model is overloaded. Please try again later. | Unavailable; retry, not invalid configuration |
| Gemini-shaped / 504 | DEADLINE_EXCEEDED | Deadline expired before operation could complete. | Timeout; retry |

## 6. Synthetic compatibility and negative cases

These are proposed classifier fixtures, not observations attributed to mockllm or a real provider. They model the user's reported generic-status/message-only gateway behavior and guard against broad matching regressions.

| Evidence | Expected classification / policy |
| --- | --- |
| HTTP 503 + generic server_error + `Invalid API key provided` | Authentication; stop despite 503 |
| HTTP 503 + `Unsupported parameter: temperature` | Parameter rejection; stop |
| HTTP 503 + `Maximum context length exceeded` | Context recovery; do not resend unchanged input |
| HTTP 503 + `Insufficient account balance` | Billing exhaustion; stop |
| HTTP 503 + `invalid upstream response` | Keep service/HTTP baseline; `invalid` does not imply user configuration |
| HTTP 503 + `The model is temporarily unavailable` | Unavailable; retry |
| HTTP 429 + insufficient_quota | Billing exhaustion; stop |
| HTTP 429 + message only `Resource limit reached` | Ambiguous limiting; HTTP-based retry, no billing-specific wording |
| HTTP 400 + `max_tokens must be less than 8192` | Output parameter rejection; stop, not API key or automatic compression |
| HTTP 400 + `image_url must be a valid URL` | Invalid argument; do not claim images are unsupported |
| HTTP 400 + `Invalid signature for authentication token` | Authentication/signing failure; not thought-signature compatibility |
| No HTTP status + `token` or `limit` only | Unknown; no invented diagnosis |
| HTTP 503 + quoted request parameters mentioning temperature | Ignore echoed parameter occurrence; keep status baseline |
| HTTP 503 + `temperature is supported; upstream connection failed` | Transient baseline; field mention/positive statement is not rejection |
| Caller token canceled and SDK timeout/cancellation represents that cooperative cancellation | Caller cancellation; no retry, assistant error banner, or injected LLM notice; persist the stopped marker |
| Unrelated request failure races with later caller cancellation | Do not relabel the real failure solely because the token is now canceled |
| Google/Mistral connector transport HttpRequestException, with no actual HTTP response | Patched helper preserves transport evidence without fabricating HTTP 400; classify through the common transport path |
| ClientResultException(status=0) wrapping a transport failure | Classify the inner transport cause; not automatically EmptyResponse |
| HTTP 200 SSE error event with explicit error evidence | Classify the event; HTTP success does not imply request success |
| Clean enumeration end with explicit output-length finish reason | Preserve useful partial output; no identical-input transport retry |

Keep rejected alternatives in test expectations where useful: the bug is often a plausible but wrong category, not failure to recognize any error.

## 7. Evolution and verification boundary

The source-backed corpus feeds the real-HTTP harness described in [Testing.md](Testing.md). Build that harness alongside first-stage SDK repairs so these bodies pass through actual SDK parsers before the application classifier is refactored. The server preserves evidence; later classifier tests assert its interpretation. Do not copy mockllm's full model validation tables or infer capabilities from model names in error classification.

When implementation begins, run fixtures through the real normalization entry point and representative SDK wrappers, not only an isolated string helper. Include missing bodies, malformed JSON, conflicting status/body, generic code values, and wrapper-chain behavior. This exposes early-return bugs that pure keyword tests miss. Request replay correctness and tool-result pairing require separate integration coverage; a correct error label does not establish safe recovery.

No claim is made that these heuristics cover all services. Future sanitized production examples should carry provenance and an expected outcome; rule changes should include a counterexample preventing the next overly broad match.
