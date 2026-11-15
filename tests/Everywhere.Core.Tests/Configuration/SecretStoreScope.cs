using System.Reflection;
using Everywhere.Interop;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

// Migrations and settings validation share a process-wide vault. These tests
// must be non-parallel and must never use the user's credentials.
internal sealed class SecretStoreScope : IDisposable
{
    public NativeSecretStore Store { get; } = Substitute.For<NativeSecretStore>();

    private static readonly FieldInfo InstanceField =
        typeof(NativeSecretStore).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(nameof(NativeSecretStore));

    private readonly object? _previousStore;

    public SecretStoreScope()
    {
        _previousStore = InstanceField.GetValue(null);
        InstanceField.SetValue(null, Store);
    }

    public void Dispose()
    {
        InstanceField.SetValue(null, _previousStore);
    }
}
