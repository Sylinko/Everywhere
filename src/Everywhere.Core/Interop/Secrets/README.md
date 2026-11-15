# Native credential storage

Each desktop entry point initializes `NativeSecretStore` before settings or account
startup. The backend operates on the same items as released versions; changing the
API does not require copying credentials into a new store.

| Platform | Identity and stored value |
| --- | --- |
| Windows | Generic Credential Manager target `service/account`, with the account as the user name and an enterprise-persistent UTF-8 blob (at most 2560 bytes). |
| macOS | File-based Keychain generic password identified by service and account, with UTF-8 data. The .NET Security queries explicitly select this Keychain rather than the Data Protection Keychain. |
| Linux | Secret Service generic schema `org.freedesktop.Secret.Generic`, with string attributes `service` and `account`. Writes use the default collection. |

Native calls are synchronous and may prompt. A missing item reads as null and can be
deleted again; an unavailable store or denied operation is an error. Enumeration
returns account names for one service. On macOS it requests attributes, not password
data. On Linux it does not unlock items; deletion addresses each match directly
because libsecret's password-clear API silently skips locked items.

## Checking an upgrade

Use an isolated service and synthetic values. Write a non-ASCII password with the
released library, read and update it with the new backend, then read it with the
released library again. After deletion, neither reader should find the item.
On macOS run the check with the normal application signing identity and file-based
Keychain. On Linux also check a locked desktop keyring and a session without a
Secret Service.

## Attribution

The Windows and Linux implementations were adapted from
[GnomeStack.Os.Secrets 0.1.3](https://github.com/gnomestack/dotnet/tree/f90dd8d76eedebc7de4f597cdfcdc6360031bf29/lib/Os.Secrets/src).
The original MIT notice is in [LICENSE-GnomeStack.txt](LICENSE-GnomeStack.txt).
