# Pre-Harness v2 node

`v2-signed-tool.msgpack` was captured with `ChatPayloadCapture.Capture` from the unmodified Windows desktop assembly at Everywhere revision `ebd37359`. It contains synthetic text, a signed tool call with a nullable argument, its result, and an opaque byte entry in assistant metadata. No application or account data is included.

The sync fixture extends this node and checks the frozen upload against the old representation. Keep the binary fixed: regenerating it with the serializer under test would remove the independent compatibility check.
