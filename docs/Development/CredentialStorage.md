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

## Explicit deletion checks the current settings

Opening the list shows names and masks without reading the vault. Revealing,
copying or editing a key reads only that row's secret.

Use **Add** to create a key, or the trash button on a row to delete just that
key. After confirmation, `Settings.RemoveApiKeyAsync` queries the
current custom assistants, four system assistants and all configured search
providers, including inactive providers. Users must reassign a key that is
still in use. There is no subscription, reverse index or vault-wide cleanup.

Removing a row keeps the native item if another row owns the same key ID.
For the last owner, native deletion must succeed before the row is removed.
If the store refuses the operation, the row remains available for retry.
Importing or replacing settings is not a deletion request.

Explicit deletion runs off the UI thread because a native call may prompt or block.
