using System.Collections.ObjectModel;
using Everywhere.AI;
using Everywhere.Common;
using Tabler.Avalonia;

namespace Everywhere.Configuration;

/// <summary>
/// Describes a configuration entry referencing an <see cref="ApiKey"/> for confirmation UI presentation.
/// </summary>
public sealed record ApiKeyReference(
    IDynamicLocaleKey NameKey,
    ColoredIcon? Icon = null,
    string? IconUrl = null);

/// <summary>
/// Provides extension methods for Settings.
/// </summary>
public static class SettingsExtensions
{
    extension(Settings settings)
    {
        /// <summary>
        /// Finds the custom assistants using a prompt, including the built-in default (Guid.Empty).
        /// </summary>
        public CustomAssistant[] FindAssistantsUsingPrompt(Guid promptId)
        {
            return settings.Model.CustomAssistants.AsValueEnumerable().Where(assistant => assistant.SystemPromptId == promptId).ToArray();
        }

        /// <summary>
        /// Lists persisted assistant and search configurations that still name this key.
        /// Inactive configurations count too: changing provider must not destroy their key.
        /// </summary>
        public IReadOnlyList<IDynamicLocaleKey> FindApiKeyUses(Guid id)
        {
            var uses = new List<IDynamicLocaleKey>();
            VisitApiKeyUses(settings, id, references: null, uses, shouldClear: false);
            return uses;
        }

        /// <summary>
        /// Lists configurations (with presentation icons) that will lose their API key if
        /// <paramref name="key"/> is removed from <paramref name="keys"/>.
        /// Duplicate imported rows sharing the same credential ID do not break references.
        /// </summary>
        public IReadOnlyList<ApiKeyReference> FindApiKeyReferences(ObservableCollection<ApiKey> keys, ApiKey key)
        {
            if (!settings.IsLastApiKeyOwner(keys, key))
            {
                return [];
            }

            var references = new List<ApiKeyReference>();
            VisitApiKeyUses(settings, key.Id, references, uses: null, shouldClear: false);
            return references;
        }

        /// <summary>
        /// Returns <see langword="true"/> when removing <paramref name="key"/> from <paramref name="keys"/>
        /// removes the last settings entry referencing <see cref="ApiKey.Id"/>.
        /// </summary>
        public bool IsLastApiKeyOwner(ObservableCollection<ApiKey> keys, ApiKey key)
        {
            var modelKeys = settings.Model.ApiKeys;
            var searchKeys = settings.Plugin.WebSearchEngine.ApiKeys;
            if (!ReferenceEquals(keys, modelKeys) && !ReferenceEquals(keys, searchKeys))
            {
                return false;
            }

            var otherKeys = ReferenceEquals(keys, modelKeys) ? searchKeys : modelKeys;
            return !keys.Any(entry => !ReferenceEquals(entry, key) && entry.Id == key.Id) && !otherKeys.Contains(key);
        }

        /// <summary>
        /// Removes an explicitly selected API key. When <paramref name="clearReferences"/> is <see langword="true"/>,
        /// configurations referencing the last owner have their key reset to <see cref="Guid.Empty"/> once native
        /// deletion succeeds so a failed deletion keeps both the row and its references intact for retry.
        /// </summary>
        public async Task RemoveApiKeyAsync(ObservableCollection<ApiKey> keys, ApiKey key, bool clearReferences = false)
        {
            var modelKeys = settings.Model.ApiKeys;
            var searchKeys = settings.Plugin.WebSearchEngine.ApiKeys;
            if (!ReferenceEquals(keys, modelKeys) && !ReferenceEquals(keys, searchKeys))
            {
                return;
            }

            var index = FindSelectedRow();
            if (index < 0) return;

            var isLastOwner = settings.IsLastApiKeyOwner(keys, key);
            if (isLastOwner)
            {
                if (!clearReferences && settings.FindApiKeyUses(key.Id) is { Count: > 0 } uses)
                {
                    throw new HandledException(
                        new InvalidOperationException("The API key is still referenced by a configuration."),
                        new FormattedDynamicLocaleKey(LocaleKey.ManageApiKeyForm_KeyInUse, new AggregateDynamicLocaleKey(uses)));
                }

                await Task.Run(() => ApiKey.DeleteKey(key.Id));
                index = FindSelectedRow();
                if (clearReferences)
                {
                    VisitApiKeyUses(settings, key.Id, references: null, uses: null, shouldClear: true);
                }
            }

            if (index >= 0) keys.RemoveAt(index);

            // ApiKey.Equals compares IDs. Remove the selected row, not an equal
            // replacement that appeared while the native store was prompting.
            int FindSelectedRow()
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    if (ReferenceEquals(keys[i], key)) return i;
                }

                return -1;
            }
        }
    }

    private static void VisitApiKeyUses(
        Settings settings,
        Guid id,
        ICollection<ApiKeyReference>? references,
        ICollection<IDynamicLocaleKey>? uses,
        bool shouldClear)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        foreach (var assistant in settings.Model.CustomAssistants)
        {
            if (assistant.Configuration.ApiKey != id) continue;

            var nameKey = new DirectLocaleKey(string.IsNullOrWhiteSpace(assistant.Name) ? assistant.Id.ToString() : assistant.Name);
            uses?.Add(nameKey);
            references?.Add(new ApiKeyReference(nameKey, Icon: assistant.Icon ?? CreateDefaultTablerIcon(TablerIconKind.Robot)));
            if (shouldClear) assistant.Configuration.ApiKey = Guid.Empty;
        }

        VisitSystemAssistant(settings.SystemAssistant.TitleGeneration, LocaleKey.SystemAssistantSettings_TitleGeneration_Header);
        VisitSystemAssistant(settings.SystemAssistant.ToolApproval, LocaleKey.SystemAssistantSettings_ToolApproval_Header);
        VisitSystemAssistant(settings.SystemAssistant.DefaultSubagent, LocaleKey.SystemAssistantSettings_DefaultSubagent_Header);
        VisitSystemAssistant(settings.SystemAssistant.ImageUnderstanding, LocaleKey.SystemAssistantSettings_ImageUnderstanding_Header);

        foreach (var provider in settings.Plugin.WebSearchEngine.Providers.Values)
        {
            switch (provider)
            {
                case ApiKeyWebSearchEngineProvider required when required.ApiKey == id:
                    AddSearchProviderReference(provider);
                    if (shouldClear) required.ApiKey = Guid.Empty;
                    break;
                case OptionalApiKeyWebSearchEngineProvider optional when optional.ApiKey == id:
                    AddSearchProviderReference(provider);
                    if (shouldClear) optional.ApiKey = Guid.Empty;
                    break;
            }
        }

        void VisitSystemAssistant(SystemAssistant assistant, string titleKey)
        {
            if (assistant.Configuration.ApiKey != id) return;

            var nameKey = new DynamicLocaleKey(titleKey);
            uses?.Add(nameKey);
            references?.Add(new ApiKeyReference(nameKey, Icon: CreateDefaultTablerIcon(TablerIconKind.Sparkles)));
            if (shouldClear) assistant.Configuration.ApiKey = Guid.Empty;
        }

        void AddSearchProviderReference(IWebSearchEngineProvider provider)
        {
            uses?.Add(provider.HeaderKey);
            references?.Add(new ApiKeyReference(provider.HeaderKey, IconUrl: provider.IconUrl));
        }

        static ColoredIcon CreateDefaultTablerIcon(TablerIconKind kind) => new(ColoredIconType.Tabler) { Kind = kind };
    }
}
