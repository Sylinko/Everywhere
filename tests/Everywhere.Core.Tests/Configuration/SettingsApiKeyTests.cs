using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

[NonParallelizable]
public sealed class SettingsApiKeyTests
{
    [Test]
    public async Task RemoveApiKeyAsync_WhenTheVaultRefusesDeletion_KeepsTheRowForRetry()
    {
        var settings = new Settings(Substitute.For<IServiceProvider>());
        var key = new ApiKey();
        settings.Model.ApiKeys.Add(key);
        using var scope = new SecretStoreScope();
        scope.Store.When(store => store.DeleteSecret(ApiKey.ServiceName, key.Id.ToString("N")))
            .Do(_ => throw new IOException("Keyring is locked"));

        await Assert.ThrowsAsync<IOException>(() => settings.RemoveApiKeyAsync(settings.Model.ApiKeys, key));

        Assert.That(settings.Model.ApiKeys.Single(), Is.SameAs(key));
    }

    [Test]
    public async Task RemoveApiKeyAsync_WhenConfigurationsStillUseTheKey_PreservesTheCredential()
    {
        var settings = new Settings(Substitute.For<IServiceProvider>());
        var key = new ApiKey();
        settings.Model.ApiKeys.Add(key);
        using var scope = new SecretStoreScope();
        scope.Store.GetSecret(ApiKey.ServiceName, key.Id.ToString("N")).Returns("validation-only");

        var assistant = new CustomAssistant { Name = "Research" };
        assistant.Configuration.ApiKey = key.Id;
        settings.Model.CustomAssistants.Add(assistant);
        settings.SystemAssistant.TitleGeneration.Configuration.ApiKey = key.Id;
        var search = settings.Plugin.WebSearchEngine;
        search.SelectedProviderId = WebSearchEngineProviderId.Official;
        ((ApiKeyWebSearchEngineProvider)search.Providers[WebSearchEngineProviderId.Brave]).ApiKey = key.Id;
        ((OptionalApiKeyWebSearchEngineProvider)search.Providers[WebSearchEngineProviderId.AnySearch]).ApiKey = key.Id;

        await Assert.ThrowsAsync<HandledException>(() => settings.RemoveApiKeyAsync(settings.Model.ApiKeys, key));

        Assert.That(settings.FindApiKeyUses(key.Id), Has.Count.EqualTo(4));
        Assert.That(settings.Model.ApiKeys.Single(), Is.SameAs(key));
        scope.Store.DidNotReceive().DeleteSecret(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task RemoveApiKeyAsync_WhenImportedRowsShareAnId_DeletesOnlyTheLastOwnersCredential()
    {
        var settings = new Settings(Substitute.For<IServiceProvider>());
        var modelKey = new ApiKey();
        var duplicate = new ApiKey { Id = modelKey.Id };
        var searchKey = new ApiKey { Id = modelKey.Id };
        settings.Model.ApiKeys = [modelKey, duplicate];
        settings.Plugin.WebSearchEngine.ApiKeys.Add(searchKey);
        using var scope = new SecretStoreScope();

        await settings.RemoveApiKeyAsync(settings.Plugin.WebSearchEngine.ApiKeys, searchKey);
        Assert.That(settings.Plugin.WebSearchEngine.ApiKeys, Is.Empty);
        await settings.RemoveApiKeyAsync(settings.Model.ApiKeys, duplicate);
        Assert.That(settings.Model.ApiKeys.Single(), Is.SameAs(modelKey));
        scope.Store.DidNotReceive().DeleteSecret(Arg.Any<string>(), Arg.Any<string>());

        await settings.RemoveApiKeyAsync(settings.Model.ApiKeys, modelKey);
        scope.Store.Received(1).DeleteSecret(ApiKey.ServiceName, modelKey.Id.ToString("N"));
        Assert.That(settings.Model.ApiKeys, Is.Empty);
    }
}
