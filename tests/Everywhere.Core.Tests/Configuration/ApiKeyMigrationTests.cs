using System.Text.Json.Nodes;
using Everywhere.Configuration;
using Everywhere.Configuration.Migrations;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

[NonParallelizable]
public sealed class ApiKeyMigrationTests
{
    private const string OldService = "com.sylinko.everywhere.apikeys";

    [Test, Platform("Win")]
    public void Windows059_WhenAKeyConflictsOrCannotBeCopied_PreservesBothOriginals()
    {
        using var storeScope = new SecretStoreScope();
        var store = storeScope.Store;
        var account = Guid.NewGuid().ToString("N");
        var conflict = Guid.NewGuid().ToString("N");
        store.EnumerateAccounts(OldService).Returns([conflict, account]);
        store.GetSecret(OldService, conflict).Returns("older");
        store.GetSecret(ApiKey.ServiceName, conflict).Returns("different");
        store.GetSecret(OldService, account).Returns("old-value");
        store.GetSecret(ApiKey.ServiceName, account).Returns((string?)null);
        store.When(item => item.SetSecret(ApiKey.ServiceName, account, "old-value"))
            .Do(_ => throw new IOException("write failed"));
        var migration = new _20260106195452_0_5_9();

        Assert.Throws<IOException>(() => migration.Migrate(new JsonObject()));

        store.DidNotReceive().SetSecret(ApiKey.ServiceName, conflict, Arg.Any<string>());
        store.DidNotReceive().DeleteSecret(OldService, Arg.Any<string>());
    }

    [Test, Platform("Win,MacOsX")]
    public void LegacyAccounts_Migrate_OnlyMovesListedKeysWithoutConflicts()
    {
        var modelKey = Guid.NewGuid();
        var searchKey = Guid.NewGuid();
        var conflict = Guid.NewGuid();
        var unlisted = Guid.NewGuid();
        var settings = JsonNode.Parse($$"""
            {
              "Model": { "ApiKeys": [{ "Id": "{{modelKey}}" }, { "Id": "{{conflict}}" }] },
              "Plugin": { "WebSearchEngine": { "ApiKeys": [{ "Id": "{{searchKey}}" }] } }
            }
            """) as JsonObject ?? throw new InvalidDataException();
        var modelAccount = modelKey.ToString("N");
        var searchAccount = searchKey.ToString("N");
        var conflictAccount = conflict.ToString("N");
        using var scope = new SecretStoreScope();
        var store = scope.Store;
        var unrecognized = new[] { unlisted.ToString("N"), modelKey.ToString("D") };
        store.EnumerateAccounts(OldService).Returns([modelAccount, searchAccount, conflictAccount, ..unrecognized]);
        store.GetSecret(OldService, modelAccount).Returns("model");
        store.GetSecret(ApiKey.ServiceName, modelAccount).Returns((string?)null);
        store.GetSecret(OldService, searchAccount).Returns("search");
        store.GetSecret(ApiKey.ServiceName, searchAccount).Returns("search");
        store.GetSecret(OldService, conflictAccount).Returns("older");
        store.GetSecret(ApiKey.ServiceName, conflictAccount).Returns("different");
        var migration = new _20261115200000_0_9_0_canary_20261115_28();

        Assert.That(migration.Migrate(settings), Is.True);

        Received.InOrder(() =>
        {
            store.SetSecret(ApiKey.ServiceName, modelAccount, "model");
            store.DeleteSecret(OldService, modelAccount);
        });
        store.Received().DeleteSecret(OldService, searchAccount);
        store.DidNotReceive().SetSecret(ApiKey.ServiceName, searchAccount, Arg.Any<string>());
        store.DidNotReceive().SetSecret(ApiKey.ServiceName, conflictAccount, Arg.Any<string>());
        store.DidNotReceive().DeleteSecret(OldService, conflictAccount);
        foreach (var account in unrecognized)
        {
            store.DidNotReceive().GetSecret(OldService, account);
            store.DidNotReceive().DeleteSecret(OldService, account);
        }
        store.DidNotReceive().EnumerateAccounts(ApiKey.ServiceName);
        store.DidNotReceive().DeleteSecret(ApiKey.ServiceName, Arg.Any<string>());
    }
}
