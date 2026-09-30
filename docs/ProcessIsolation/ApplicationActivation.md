# Application activation

On Windows, Main owns a dedicated `main-activation` endpoint for secondary launches,
registered URL callbacks, and window activation. This endpoint uses the process-isolation
RPC transport but is neither a Host role nor the Hosts controller endpoint. macOS uses
native application events instead. OAuth state, PKCE, and token exchange remain in
Main's cloud client.

`Everywhere.ProcessIsolation` owns the activation contracts, client, server, and transport.
`Everywhere.Windows.ProcessIsolation.ApplicationActivationEntrance` owns Windows
command-line activation bootstrap and failure presentation. Core owns shared runtime
initialization and optional activation delivery to the UI. Linux startup integration
is outside this Windows/macOS contract.

## Bootstrap and ownership

Role and controller dispatch precedes Main's single-instance arbitration. Each platform
entry point owns that arbitration and the secondary-launch policy. Only a primary
calls `Entrance.Initialize`, which initializes runtime constants, telemetry, logging,
and exception handling without depending on an activation service.

The Windows entry point uses its platform-local `ApplicationActivationEntrance`:
`TryCreateServer` claims the activation endpoint and queues an initial URL before
runtime initialization; `SendToPrimaryAsync` forwards a URL or chat-window request
only when the entry point chooses to activate the existing Main. Windows `Program`
checks `--autorun` before forwarding a secondary launch. Core and macOS do not depend
on this Windows bootstrap helper.

Endpoints share the user/session identity used by the Host endpoints. On Windows,
the user component is the token's user SID, including when UAC supplies a filtered
or elevated token for the same account. A different administrator account is a
different identity and is not allowed to forward into this Main instance.

Windows uses the first-pipe-instance flag as the Main claim, replacing the named
application mutex. The shared server also supports a Unix endpoint ownership lease;
that transport capability does not provide a platform's application startup policy.
An inaccessible Windows endpoint is never interpreted as permission to start a
second Main: the secondary path either forwards successfully or exits with failure.

The activation server retains its original listening handle across connections.
Each RPC session leaves that stream open after its reader and writer stop; the
server disconnects the completed session and accepts the next client. This preserves
ownership throughout idle periods and session replacement. A terminal listener
failure completes the pending queue with an error and causes Main to shut down.

The platform entry point retains the activation server in an asynchronous disposal
scope. A null claim result takes the secondary path immediately, without constructing
the production application graph. The primary path passes the non-null server into
Main startup. No startup-result wrapper or endpoint-less primary object is needed.
Runtime initialization failures unwind the same disposal scope as normal shutdown.

Windows registers the existing `ApplicationActivationServer` instance directly in DI.
App uses the server only when registered; its startup and shutdown do not require one.
After Avalonia exits, Main stops activation sessions and delivery before DI cleanup,
while retaining the endpoint claim. DI does not dispose this externally supplied
instance. Final entry-point disposal releases the endpoint after Main cleanup.

## Native macOS activation

macOS does not create an activation endpoint or register an
`ApplicationActivationServer`. After claiming its single-instance lifetime, its entry
point calls `Entrance.Initialize` for shared Main initialization, independently of RPC
activation. Host and controller dispatch still precede this initialization; Input,
Automation, and Hosts-control RPC remain unchanged.

The application bundle declares its URL scheme in `Info.plist`. Launch Services
activates the running application and sends reopen or get-URL Apple events, which
`AppDelegate` maps directly to Core-local application messages. This restores the
previous native callback path without adding a common activation queue.

Direct executable and LaunchAgent starts retain the previous named-object claim
`com.sylinko.everywhere`. The entry point keeps the mutex handle without acquiring
the mutex, so ownership is independent of the thread performing asynchronous cleanup.
The claim remains alive until Main and DI cleanup finish. Direct secondary launches
print `Everywhere is already running.` and return 0, without launching another process
or forwarding their arguments. Native bundle reopen and URL events remain the
application activation path. The macOS LaunchAgent executes the bundle without
Windows-specific `--autorun` arguments; old LaunchAgent arguments have no special
meaning on macOS. Secondary processes do not initialize the production graph or
start an activation listener.

## Security and protocol

`NamedPipeEndpoint` supplies the shared Host, Main-control, and activation pipe
security policy. Windows uses a protected current-user DACL and applies only
`LABEL_SECURITY_INFORMATION` to establish medium integrity, allowing an ordinary
same-user activation client to write to an elevated Main. This does not grant
access to arbitrary users or low-integrity browser sandbox processes. The browser
launches the registered application executable as the forwarding client.

The client and server verify the OS-reported peer before starting RPC. Windows
reuses its platform image verifier. Windows may omit an explicit medium label;
an unlabeled object has effective medium integrity according to
[Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control).

The client claims the closed `application-activation` wire identity, and the server
claims `main`. Both sides enforce the existing exact-build and desktop-session
handshake. A newer executable cannot forward into a still-running older Main;
version mismatches are explicit failures rather than a second Main launch.

`IApplicationActivationRpc` accepts a MessagePack union of two concrete
`ApplicationActivationRequest` classes:

- `ShowChatWindowActivationRequest`, with no URL or arbitrary navigation route;
- `UrlCallbackActivationRequest`, with an absolute `sylinko-everywhere` URL of at most 16,384 UTF-16
  code units. Scheme comparison is case-insensitive.

Union tag 0 identifies `ShowChatWindowActivationRequest` with an empty array payload. Tag 1 identifies
`UrlCallbackActivationRequest`, whose payload key 0 is a non-null URL. No enum discriminator or optional
URL is exposed to callers. URL shape, scheme, and length are still validated before
queue acceptance: deserialization can supply null despite the C# nullable contract.
Unknown union tags are rejected by the RPC payload codec. The exact-build handshake
means the previous enum-based wire representation needs no compatibility adapter.

Core's `ApplicationMessage` remains an in-process message hierarchy. In particular,
`ShowWindowMessage.Route` can contain a live UI object. Activation requests carry only
the external command data and are mapped to Core messages after validation and startup;
the RPC contract does not serialize arbitrary UI routes or depend on Core.

Frames are limited to 64 KiB. The listener has a ten-second session deadline, in
addition to the transport's handshake and partial-frame deadlines. A stalled or
rejected client does not terminate the listener or block subsequent clients forever.

## Acceptance and delivery

The server owns a bounded queue of 32 requests. RPC and an initial command-line URL
use the same validation and queue. Ordinary primary launches retain their existing
window visibility behavior.

Windows login startup is registered in the current user's Run registry key as the
application executable with `--autorun`. It follows the normal Main entry and endpoint
arbitration: if Main already owns the endpoint, the secondary exits without sending
an activation, even when a URL is also present; otherwise Main initializes normally.
`--autorun` is interpreted only in the Windows secondary path. It does not suppress
primary window display: `--ui`, first-launch and upgrade behavior, and version-marker
updates follow the existing window policy.

The response is one of `Accepted`, `InvalidRequest`, `Busy`, or `ShuttingDown`.
`Accepted` means Main has taken ownership of an in-memory activation, independently
of the client's subsequent lifetime. It does not mean OAuth succeeded, nor does it
provide persistence across a Main crash or intentional shutdown.

After application initializers and chat-window initialization finish, App drains
the queue and dispatches Core-local application messages on the UI thread. The
delivery worker awaits cancellable dispatcher operations, keeping retention bounded
and allowing shutdown after the desktop message loop stops. Initial callbacks cannot
recreate a lost interactive authorization flow; the cloud client handles a callback
only when it already owns a live flow.

When the platform chooses RPC forwarding, the secondary process waits for a response
and returns 0 only for `Accepted`.
Connection establishment is retried at most three times, with a three-second
deadline per attempt. Authentication and request delivery share a five-second
deadline. Once authentication starts, the request is never automatically replayed:
a lost response can leave the acceptance outcome unknown. Rejections and failures
return 1 and show a localized message. Diagnostic output and callback logging omit
the URL and peer-supplied exception text.

## Platform verification

Windows acceptance requires a real UAC matrix: ordinary Main and ordinary client;
elevated Main and ordinary browser-launched client; ordinary Main and elevated client;
elevated Main and elevated client. For each case, verify both window activation and a
complete browser OAuth flow. Also verify callback arrival during startup, shutdown,
concurrent launches, repeated callbacks, and version mismatch.

Verify that an autorun secondary exits without activation, with or without a URL,
and that an autorun primary follows normal first-launch, upgrade, and `--ui` window
policy. Check that the endpoint remains claimed throughout application and DI cleanup
and becomes available only after final entry-point disposal.

On macOS, verify native OAuth callbacks and reopen events, direct secondary starts,
concurrent cold starts, LaunchAgent login startup, and endpoint-free shutdown. A direct
secondary must print the already-running message and exit without launching another
process. A Windows build cannot establish native macOS runtime behavior.
