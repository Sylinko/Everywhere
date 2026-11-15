using System.Diagnostics;

namespace Everywhere.Interop;

/// <summary>
/// Stores application secrets in the current user's operating-system credential store.
/// Native operations are synchronous and may prompt or block.
/// </summary>
public abstract class NativeSecretStore
{
    /// <summary>
    /// Gets the platform implementation selected at startup.
    /// </summary>
    public static NativeSecretStore Instance => _instance ?? throw new InvalidOperationException("No secret store is registered.");

    private static NativeSecretStore? _instance;

    /// <summary>
    /// Sets the platform implementation once in the executable's entry point, before
    /// starting any settings or account work.
    /// </summary>
    public static void Initialize(NativeSecretStore store)
    {
        Debug.Assert(_instance is null, "The platform secret store has already been initialized.");
        _instance = store;
    }

    /// <summary>
    /// Reads a secret. Returns null only when the item does not exist.
    /// A denied or unavailable store is an error, not a missing item.
    /// </summary>
    public abstract string? GetSecret(string service, string account);

    /// <summary>
    /// Creates or replaces one secret without changing its identity.
    /// </summary>
    public abstract void SetSecret(string service, string account, string secret);

    /// <summary>
    /// Deletes one item. An already absent item is successful.
    /// </summary>
    public abstract void DeleteSecret(string service, string account);

    /// <summary>
    /// Lists only the account identifiers belonging to this service, not their secret values.
    /// </summary>
    public abstract IReadOnlyList<string> EnumerateAccounts(string service);
}
