# Credential storage

See [Native credential storage](../../src/Everywhere.Core/Interop/Secrets/README.md) for the platform layouts.
API keys keep their lowercase, unhyphenated GUID account names; OAuth keeps
`oauth_token_data` in the same `com.sylinko.everywhere` service. Neither
changes the stored value format.

## Upgrading legacy API keys

Versions 0.5.6 and 0.5.7 stored API keys in `com.sylinko.everywhere.apikeys`.
Two migrations handle different upgrade paths:

- **0.5.9 (Windows only):** On a direct upgrade from an older settings version,
  move API-key accounts from the dedicated old service, as before. This task
  recognizes the format those releases wrote: a non-empty, lowercase N-format GUID.
  It does not require the account to be listed in this settings file. If old-item
  removal fails after the replacement is safe, log it and continue with the original
  system-prompt migration.
- **0.9.0-canary.20261115.28 (Windows and macOS):** Recover only exact old-service
  accounts whose IDs appear in `Model.ApiKeys` or `Plugin.WebSearchEngine.ApiKeys`.
  No listed IDs means no vault access. Unlisted accounts may belong to another
  settings file and are left alone.

Both migrations write a missing destination before deleting the old item. If the
destination already has the same value, only the duplicate old item is removed.
Different values are left intact on both sides. Neither migration enumerates or
deletes from the current service.

The newer migration propagates native errors. The settings version stays unchanged
on failure, and a retry recognizes a copy already written before deletion failed.
