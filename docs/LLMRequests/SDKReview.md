# SDK Retry, Timeout and Cancellation Review

Source review and SDK repairs: 2026-10-02/03. The source observations below describe the pinned upstream versions; section 9 records implemented repairs and real-HTTP verification. Application retry and presentation changes remain design work.

First-stage repairs use the real-HTTP harness described in [Testing.md](Testing.md), reproducing each defect before patching and verifying the same SDK entry point afterward. This supersedes the earlier plan to defer the mock server until application retry work.

## 1. Exact source versions

Versions come from `src/Everywhere.Core/obj/project.assets.json`; source commits come from each installed NuGet package's `.nuspec`. The upstream repositories were cloned without source edits into `E:\Source\CSharp`. Azure and dotnet/extensions use sparse checkouts of relevant directories; all five checkouts were verified at the commits below with clean working trees.

| Package / source | Version | Commit | Checkout |
| --- | --- | --- | --- |
| OpenAI | 2.12.0 | 6450c84ae936944613b1844d725466c9b450fa77 | openai-dotnet |
| System.ClientModel | 1.14.0 | 3a58372182875061915cffce64f9b9d978a4ed6f | azure-sdk-for-net |
| Anthropic | 12.45.0 | 13c68f0e37cbbf1623cf7daa13d3074286cf7ffd | anthropic-sdk-csharp |
| OllamaSharp | 5.4.30 | b1b408df43a2a13a29e8db9de3130fa6f44aacc9 | OllamaSharp |
| Microsoft.Extensions.AI.OpenAI | 10.9.0 | b10f9c0a081b5dbb7755b8f5592e1d3c3f550a3a | extensions |
| Local Semantic Kernel source | Local source plus Everywhere patches, not just a NuGet version | dc7c1c048488a8b611b5382d0d7efe05608a9fa0 | Everywhere/3rd/semantic-kernel |

Context7's official Anthropic C# documentation was consulted, but the checked-out package commit is authoritative for this review. Current documentation can describe members/behavior absent from this exact version.

## 2. OpenAI Chat and Responses

Everywhere supplies endpoint and HttpClientPipelineTransport, without setting RetryPolicy or NetworkTimeout. Both OpenAI client paths construct a System.ClientModel ClientPipeline; their response-status classifiers delegate retry classification to its default rules. The inspected Microsoft.Extensions.AI.OpenAI Chat/Responses streaming adapters forward to SDK streaming calls without an additional retry loop.

At System.ClientModel 1.14.0:

- `ClientRetryPolicy.DefaultMaxRetries` is **3**, meaning up to four SDK attempts.
- Retryable response statuses are **408, 429, 500, 502, 503, 504**. The default does not classify every 5xx as retryable.
- Transport HttpRequestException is wrapped as ClientResultException with status zero. The default classifier also retries IOException and cancellation not attributed to the message's caller token.
- Default delay is exponential from **0.8 seconds**. A larger valid server retry delay takes precedence. This implementation's default delay is not the jittered application policy proposed elsewhere.
- Disabling SDK retries uses `RetryPolicy = new ClientRetryPolicy(0)` on the corresponding options type.
- `ClientPipeline.DefaultNetworkTimeout` is **100 seconds**. PipelineTransport applies it around transport processing and wraps unbuffered response content in ReadTimeoutStream. The latter starts/stops its timer for each underlying read; it is not a whole-generation deadline.
- Everywhere independently sets its supplied HttpClient.Timeout to Assistant.RequestTimeoutSeconds (default 20 seconds). Transport sends with ResponseHeadersRead. Thus the configured header/request wait and the SDK's subsequent per-read timeout are distinct constraints.
- Once a streamed response has been returned to the application, failures during subsequent SSE consumption are outside the completed pipeline retry invocation. SDK HTTP retries do not provide whole-stream replay.

Sources: [OpenAI pipeline construction](../../../openai-dotnet/OpenAI/src/Utility/OpenAIClientUtilities.cs), [retry policy](../../../azure-sdk-for-net/sdk/core/System.ClientModel/src/Pipeline/ClientRetryPolicy.cs), [classifier](../../../azure-sdk-for-net/sdk/core/System.ClientModel/src/Options/PipelineMessageClassifier.cs), [pipeline defaults](../../../azure-sdk-for-net/sdk/core/System.ClientModel/src/Pipeline/ClientPipeline.cs), [transport timeout](../../../azure-sdk-for-net/sdk/core/System.ClientModel/src/Pipeline/PipelineTransport.cs), [read timeout](../../../azure-sdk-for-net/sdk/core/System.ClientModel/src/Internal/ReadTimeoutStream.cs), [HTTP transport](../../../azure-sdk-for-net/sdk/core/System.ClientModel/src/Pipeline/HttpClientPipelineTransport.cs).

## 3. Anthropic

Everywhere passes ClientOptions.HttpClient and sets ClientOptions.Timeout from that client's configured timeout, but does not set MaxRetries.

At Anthropic 12.45.0:

- `DefaultMaxRetries` is **2**; set `MaxRetries = 0` to disable these retries.
- Response retries include **408, 409, 429 and all >= 500**, subject to the `X-Should-Retry` header override.
- Exception retries cover IOException (excluding file/directory-not-found) and AnthropicIOException. HttpRequestException is wrapped as AnthropicIOException. OperationCanceledException alone is not part of this exception retry predicate.
- Backoff is `min(0.5 * 2^(retries - 1), 8)` seconds multiplied by jitter in the range (0.75, 1]. Retry-After-Ms/Retry-After override it only for a positive delay strictly below one minute; other values fall back to local backoff, rather than being capped to one minute.
- The SDK's default timeout is ten minutes, but Everywhere overrides it. ExecuteOnce creates a timeout CTS and links caller cancellation while sending. It disposes these sources before returning the response, with an explicit source comment explaining that body reads use their own supplied tokens.
- HttpClientPassthroughHandler sends with ResponseHeadersRead. SSE parsing passes the caller token into SseParser. The configured SDK request timeout therefore does not provide a continuing idle-read timeout after response headers.
- The generic status/I/O retry loop surrounds ExecuteOnce, not later SSE enumeration. SSE error events and broken streams need application-level handling.
- This version's AnthropicApiException exposes status and response body, but no response-header collection. Retry-After/request-ID extraction cannot always be deferred until the final exception is available.

The SDK also has a separate credential-refresh path gated on its credential configuration. Everywhere currently supplies a static API key or its own authenticated HttpClient; do not confuse that optional SDK credential path with the app's OAuth handler.

Sources: [ClientOptions](../../../anthropic-sdk-csharp/src/Anthropic/Core/ClientOptions.cs), [Execute/ExecuteOnce and retry rules](../../../anthropic-sdk-csharp/src/Anthropic/AnthropicClient.cs), [HTTP forwarding](../../../anthropic-sdk-csharp/src/Anthropic/Core/HttpClientPassthroughHandler.cs), [SSE parsing](../../../anthropic-sdk-csharp/src/Anthropic/Core/Sse.cs), [API exception](../../../anthropic-sdk-csharp/src/Anthropic/Exceptions/AnthropicApiException.cs).

## 4. Google and Mistral Semantic Kernel connectors

Everywhere uses its patch projects, which mix local replacement files with upstream Semantic Kernel source. In the inspected request paths, each HTTP send calls SendWithSuccessCheckAsync; there is no generic retry loop around that send. Tool auto-invocation/continuation loops are a different mechanism and must not be counted as transport retries.

- Streaming sends use ResponseHeadersRead. The configured HttpClient timeout does not itself bound all later body reads; there is no equivalent System.ClientModel ReadTimeoutStream in these paths.
- The shared HTTP helper catches a transport HttpRequestException and creates `HttpOperationException(HttpStatusCode.BadRequest, null, e.Message, e)` even when there was no HTTP 400 response. This is synthetic status evidence, not a real server parameter rejection.
- For actual non-success responses, the helper reads the body and constructs HttpOperationException with response status/body. Its error-body read uses parameterless ReadAsStringAsync, creating another cancellation boundary to audit.
- HttpOperationException does not retain the complete response headers. Header-based retry hints need capture at the HTTP/adapter boundary before information is lost.

The planned connector patch removes the synthetic HTTP 400 at its source and preserves cancellation during error-body reads. Classification still distinguishes response evidence from transport evidence, but should not acquire a permanent special case for a connector defect we control. Existing application HTTP handlers contain no general retry/resilience handler; official authentication recovery is the separate exception described below.

Sources: [shared HTTP helper](../../3rd/semantic-kernel/dotnet/src/InternalUtilities/src/Http/HttpClientExtensions.cs), [Google base client](../../3rd/semantic-kernel/dotnet/src/Connectors/Connectors.Google/Core/ClientBase.cs), [patched Google stream client](../../3rd/semantic-kernel-patch/Connectors.Google/Core/Gemini/Clients/GeminiChatCompletionClient.cs), [patched Mistral client](../../3rd/semantic-kernel-patch/Connectors.MistralAI/Client/MistralClient.cs), [default HttpClient setup](../../src/Everywhere.Core/Initialization/NetworkInitializer.cs).

## 5. OllamaSharp

The inspected OllamaSharp 5.4.30 chat stream sends once with ResponseHeadersRead and has no generic transport retry loop. HTTP 400 is converted to an Ollama-specific error from the body; other non-success statuses go through EnsureSuccessStatusCode. The generic ProcessStreamedResponseAsync recognizes in-stream error JSON as ResponseError, but the actual chat path uses ProcessStreamedChatResponseAsync, which lacks that check. The generic helper's behavior must not be attributed to the chat path.

The application's streaming route is IChatClient.GetStreamingResponseAsync -> ChatAsync -> ProcessStreamedChatResponseAsync. The latter checks the caller token in the loop condition but awaits **parameterless ReadLineAsync()**; the completion and generic stream readers have the same cancellation defect. Cancellation of a token while a line read is blocked is not observed by that loop condition until the read returns. The caller's CancellationToken cannot be assumed to interrupt that pending body read merely because it was supplied to the earlier HTTP send. On a loop-boundary cancellation the iterator can also end without throwing, so the request consumer must check caller cancellation before accepting apparent completion.

Use the existing compile-time patch mechanism to fix the actual chat reader and its error-body path. Pass cancellation into pending reads, propagate cooperative cancellation rather than silently completing, and retain the existing ownership/disposal boundaries. Also recognize protocol error objects before mapping them into chat updates, following the SDK's existing generic-reader behavior. Racing MoveNextAsync against a delay and abandoning the still-running read is insufficient: a new retry must not overlap an undisposed previous request. No SDK source has been edited in the research checkout.

Source: [OllamaApiClient](../../../OllamaSharp/src/OllamaSharp/OllamaApiClient.cs), especially StreamPostAsync, ProcessStreamedResponseAsync and SendToOllamaAsync.

## 6. Authentication replay and attempt counting

Everywhere's official-service CloudAuthenticationHandler buffers/clones the request, sends it, and on HTTP 401 attempts credential refresh followed by one resend. This is independent of both SDK transient retries and the proposed request retry budget.

Disabling SDK retries does **not** make one logical application attempt equal exactly one physical HTTP send. Preserve bounded credential recovery, distinguish it from transient failure retries, and describe counters accordingly. The UI's 3/5 counts application request retries, not token-refresh exchanges. A 401 still returned after authentication recovery is a terminal authentication result unless better evidence says otherwise.

Source: [OAuthCloudClient.CloudAuthenticationHandler](../../src/Everywhere.Cloud/OAuthCloudClient.cs), [official HttpClient registration](../../src/Everywhere.Cloud/ServiceExtension.cs).

## 7. Required design corrections

1. Disable SDK transient retries explicitly for both OpenAI client options and Anthropic before adding the application loop. With default application MaxRetries=5 left layered on current defaults, repeated eligible HTTP failures can produce **6 * 4 = 24** sends for OpenAI and **6 * 3 = 18** for Anthropic, even before separate authentication recovery.
2. Preserve request-scoped HTTP evidence where exception types discard it. Status provenance, Retry-After, structured code and request ID must not rely on localized text or a shared last-response field. An observer must not itself retry or retain full response bodies unnecessarily.
3. Resolve timeout ownership explicitly. Existing RequestTimeoutSeconds currently affects header/request waiting, while post-header behavior differs by SDK. Do not silently treat it as a total stream deadline or equate byte-read timeout with time to a meaningful assistant token.
4. Keep initial integration behavior changes explicit: setting OpenAI NetworkTimeout to RequestTimeoutSeconds would also change its current 100-second read timeout, while imposing a new streaming idle limit on Anthropic/Google/Mistral/Ollama adds behavior they do not currently share. These require a deliberate timeout contract before implementation.
5. Fix non-interruptible reads and uncancelable error-body reads in the actual adapter/transport boundaries. A reusable retry executor cannot repair these by merely passing CancellationToken again.
6. Remove the connector's synthetic HTTP 400 wrapper through its maintained source replacement. Treat OpenAI's status-zero wrapper as transport evidence, not automatically an empty response.
7. Keep completion/cancellation checks after enumeration. A normal iterator end can reflect token-checked exit or incomplete protocol termination rather than a valid completed response.

Static source analysis establishes the mechanisms above. Section 9 records runtime probes of selected paths; these do not establish the future application retry/timeout contract or every provider behavior.

## 8. Patch ownership and delivery

Follow [the repository's dependency patching architecture](../patches.md). Fix a confirmed SDK defect at the narrowest maintained dependency boundary; KernelMixin owns application retry policy and consumes faithful transport/protocol evidence. It does not compensate for known, locally repairable defects with scattered provider-specific branches.

| Finding | Planned treatment |
| --- | --- |
| OpenAI and Anthropic hidden transient retries | Use their existing options to disable them; no binary patch is needed. |
| Google/Mistral synthetic HTTP 400 and uncancelable error-body reads | Replace the shared HTTP helper through the existing mirror projects. Preserve the original transport exception, forward the token into body reads, and do not wrap cooperative cancellation in HttpOperationException. Dispose failed responses on all exceptional exits. |
| Ollama chat reader ignores cancellation while awaiting a line, and does not recognize protocol error objects | Add a narrow donor patch for OllamaSharp using the existing static IL weaving pipeline. Preserve chat/done mapping and disposal while correcting reads and error propagation. Cover the actual IChatClient streaming route. |
| Responses streaming rejects missing call_id before the application wrapper can repair it | Normalize null/empty IDs in the MEAI string ParseCallContent overload using a static donor wrapper. Retain its original argument parser and existing valid IDs. This is compatibility with malformed upstream responses, not a relaxation of the wire protocol. |
| SDK exceptions discard retry headers/request identifiers | The modified Google/Mistral helper and Ollama failure path store selected headers in Exception.Data. Anthropic's terminal API-exception creation needs a donor because its factory accepts only status/body. OpenAI already exposes its raw response headers. No shared response cache is needed. |
| Explicit finish/error evidence disappears during conversion | Preserve MEAI FinishReason in SK streaming metadata, including updates carrying usage. Preserve each Mistral choice's finish reason. Replace the complete MEAI Responses stream conversion to retain incomplete reasons/usage and failed-response errors. Reuse original mappings and helpers. |
| Different initial/streaming timeout semantics | Define the application timeout contract first, then configure or adapt each provider. A semantic difference is not automatically a defect requiring a patch. |

Google and Mistral both import InternalUtilities.props, which includes the upstream helper through a glob. A shared local replacement must explicitly remove that Compile item after the import and include the replacement in both projects (Mistral disables default compile items). Patching only Microsoft.SemanticKernel.Abstractions would not change these connector-local copies.

The current [Build.Patches.targets](../../src/Build.Patches.targets) keeps ReferencePath unchanged for compilation and replaces ReferenceCopyLocalPaths plus publish/runtime items. Donor projects are build dependencies, not runtime references. Research checkouts outside Everywhere are not shipping build dependencies; changes belong in the repository's mirror or donor projects.

Validate through the actual patched assemblies, following [the existing Semantic Kernel patch test project](../../tests/Everywhere.Patches.SemanticKernel.Tests/Everywhere.Patches.SemanticKernel.Tests.csproj): a blocked chat read cancels and releases its response; an error-body read cancels without becoming HTTP 400; a transport failure retains its original evidence; an Ollama error object is a failure, not empty output. Also verify replacement reaches published output. The current weave task does not explicitly fail when a declared target is absent from all resolved references; do not claim that successful compilation alone proves every intended patch was applied. Check the expected target/method and patched behavior when dependencies change.

## 9. Implemented repairs and verification

Google/Mistral now use a shared mirror replacement that preserves transport exceptions, cancels error-body reads, disposes failed responses, and captures selected retry/request-ID headers in exception data. The OllamaSharp donor fixes pending reads in all three streaming readers, recognizes error objects, and preserves HTTP failure evidence. Production SDK retry defaults remain unchanged until the application executor owns the budget.

### HTTP evidence and response ownership

Google/Mistral, Ollama and Anthropic attach only `Retry-After`, `Retry-After-Ms`,
`request-id` and `x-request-id` under `Exception.Data["Everywhere.Http." + name]`.
The exception retains its SDK subtype and status/body representation; Ollama also
needs explicit status/body data because its HTTP 400 exception has neither field.
Unrelated headers are not copied. OpenAI's existing `ClientResultException`
response extension point retains its headers without a new donor.

Anthropic's `HttpResponse` has header access, but its API exception factory only
accepts status and body. An HttpClient handler cannot directly attach those headers
to the SDK-created terminal exception. The donor therefore copies the complete
`Execute<T>` request loop and adds evidence at that creation point. The original
retry predicates/backoff, credential refresh, exception wrapping and disposal are
retained; original interface implementation flags are also preserved during weaving.

Response observers forward the actual content and stream without changing bytes or
errors. Failure-path tests require content disposal before emergency teardown.
Stream tests accept content or stream disposal: OpenAI's pipeline owns the response
stream and can release it without disposing the enclosing HttpResponseMessage.

### Completion evidence

The real HTTP boundary tests identified three conversion losses:

| Boundary | Repair |
| --- | --- |
| MEAI to SK streaming content | Copy an explicit FinishReason into metadata, preserving it when the same update includes usage. Do not mutate provider-owned AdditionalProperties. |
| Mistral to SK streaming content | Carry each choice's FinishReason alongside the existing chunk metadata. |
| Responses to MEAI streaming updates | Map response.incomplete's reason and usage with original helpers; expose response.failed's error as the existing ErrorContent type. Preserve all other original event cases. |

The Responses replacement copies the full upstream conversion, including tool
content, annotations, reasoning and continuation handling. Private helpers remain
the original methods. Source links and class-level documentation explain each
donor's purpose; inline comments identify Everywhere modifications.

A clean HTTP EOF without a terminal event still produces no explicit completion
reason in the six tested paths. An interrupted HTTP body instead raises an error.
The adapter does not infer success from EOF; a robust application completion check
remains part of the later request executor. ChatService behavior is unchanged.

### Responses call IDs

The OpenAI SDK accepts a function-call item whose call_id is absent or null. MEAI 10.9.0 then calls its string ParseCallContent overload at response.output_item.done. FunctionCallContent.CreateFromParsedArguments rejects a null ID before parsing arguments, so OptimizedChatClient never receives the update. Empty IDs survive that check but are not usable identifiers.

[The MEAI donor](../../patches/Everywhere.Patches.MicrosoftExtensionsAI.OpenAI/patch_OpenAIClientExtensions.cs) supplies a fresh version-7 GUID only for null/empty call_id, then invokes orig_ParseCallContent. It preserves valid IDs, original argument parsing, and captured FunctionCallContent.Exception. The BinaryData overload and constructor validation remain unchanged. In this pinned adapter version, only Responses streaming uses the string overload; SDK upgrades require reviewing that call-site boundary as well as the signature checked by MonoModRules.

Responses streaming emits one completed FunctionCallContent for each completed output item. Parallel calls, including calls to the same function, therefore receive separate generated IDs. Everywhere rebuilds full stateless history: the generated ID becomes the matching function_call.call_id and function_call_output.call_id in the next request. The former post-conversion ID repair in OptimizedChatClient is removed; its ErrorContent handling remains.

This does not recover a server-owned identifier. If Everywhere later introduces previous_response_id or server-side conversations, generated IDs cannot be assumed to identify a stored server call; that feature needs a separate compatibility policy.

### Verification on 2026-10-02

- The same six Responses HTTP regressions against the original MEAI binary produced five failures and one passing valid-ID control. With the patch, all six pass: absent/null/empty/present IDs, parallel same-function calls, and malformed arguments retaining their parsing exception. Tests inspect the second HTTP request to verify exact call/result ID pairing.
- The complete NUnit suite ran against an independently started MockServer 8.0.0 through WSL Containers in Release: **112 passed, zero skipped**. This includes prior error, cancellation, protocol, concurrency, and SDK retry-count tests.
- The published test assembly also passes all six call-ID regressions. Its MEAI DLL hash matches the woven intermediate DLL and differs from the original NuGet DLL, verifying publish replacement.
- Core Release builds with zero warnings/errors using command-line ManagePackageVersionsCentrally=true. Without that opt-in, the existing ShadUI project fails restore with NU1015/NU1107; its project configuration was not changed for this patch.
- Windows Release builds with the same opt-in; its output MEAI DLL matches that project's woven intermediate DLL. The build reports eight Watchdog/MessagePack AOT/trim warnings unrelated to this donor.
- These tests use real loopback HTTP and the actual SDKs. They do not execute application tools, call paid APIs, or verify native UI, application retry, or continuation behavior. Docker CI has not been run locally.

### Verification on 2026-10-03

- The complete Release suite passes **163 tests, zero skipped**, through the WSLc runner, including automatic startup and cleanup of its dedicated MockServer 8.0.0 container.
- New boundary cases cover finish reason/usage, Responses incomplete/failed events, selected headers, interrupted error-body reads, and disposal. The before-repair run exposed the conversion and Anthropic header losses; the targeted repaired suite passes all 22 selected cases.
- For all six paths, lifecycle tests distinguish normal HTTP EOF without a terminal event, an unterminated chunk followed by disconnect, a pending read canceled by the caller, and early disposal without cancellation. Response/stream release is asserted before emergency cleanup; no failed stream is silently resent.
- Existing OpenAI/Responses/Anthropic retries still recover after two 429 or 503 responses; all three outgoing request bodies are identical. Concurrent Google/Mistral/Ollama/Anthropic failures retain separate retry hints and request identifiers. Application retry is not introduced.
- Windows Release builds with zero warnings/errors using the existing command-line central-version-management opt-in.
- The published test assembly passes the same 163 cases. Anthropic, MEAI OpenAI, SK Abstractions and Ollama DLL hashes match the woven intermediate output and differ from the official NuGet binaries; Windows output matches its own woven copies.
- A deliberately empty required test selection fails, saves console/container logs, and cleans up its owned WSLc container; it cannot turn into a green integration run.
