using System.Collections.ObjectModel;
using Everywhere.AI;
using Everywhere.Common;

namespace Everywhere.Configuration;

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
            if (id == Guid.Empty)
            {
                return [];
            }

            var references = new List<IDynamicLocaleKey>();
            foreach (var assistant in settings.Model.CustomAssistants)
            {
                if (assistant.Configuration.ApiKey == id)
                {
                    references.Add(new DirectLocaleKey(assistant.Name ?? assistant.Id.ToString()));
                }
            }

            AddSystemAssistant(settings.SystemAssistant.TitleGeneration, LocaleKey.SystemAssistantSettings_TitleGeneration_Header);
            AddSystemAssistant(settings.SystemAssistant.ToolApproval, LocaleKey.SystemAssistantSettings_ToolApproval_Header);
            AddSystemAssistant(settings.SystemAssistant.DefaultSubagent, LocaleKey.SystemAssistantSettings_DefaultSubagent_Header);
            AddSystemAssistant(settings.SystemAssistant.ImageUnderstanding, LocaleKey.SystemAssistantSettings_ImageUnderstanding_Header);
            foreach (var provider in settings.Plugin.WebSearchEngine.Providers.Values)
            {
                var keyId = provider switch
                {
                    ApiKeyWebSearchEngineProvider required => required.ApiKey,
                    OptionalApiKeyWebSearchEngineProvider optional => optional.ApiKey,
                    _ => Guid.Empty
                };
                if (keyId == id)
                {
                    references.Add(provider.HeaderKey);
                }
            }
            return references;

            void AddSystemAssistant(SystemAssistant assistant, string title)
            {
                if (assistant.Configuration.ApiKey == id)
                {
                    references.Add(new DynamicLocaleKey(title));
                }
            }
        }

        /// <summary>
        /// Removes an explicitly selected, unused key. The last settings entry stays in
        /// place until native deletion succeeds, so the user can retry a failed deletion.
        /// </summary>
        public async Task RemoveApiKeyAsync(ObservableCollection<ApiKey> keys, ApiKey key)
        {
            var modelKeys = settings.Model.ApiKeys;
            var searchKeys = settings.Plugin.WebSearchEngine.ApiKeys;
            if (!ReferenceEquals(keys, modelKeys) && !ReferenceEquals(keys, searchKeys))
            {
                return;
            }

            var index = FindSelectedRow();
            if (index < 0) return;

            var uses = settings.FindApiKeyUses(key.Id);
            if (uses.Count > 0)
            {
                throw new HandledException(
                    new InvalidOperationException("The API key is still referenced by a configuration."),
                    new FormattedDynamicLocaleKey(LocaleKey.ManageApiKeyForm_KeyInUse, new AggregateDynamicLocaleKey(uses)));
            }

            // Imported rows can share the same native credential, within or across lists.
            var otherKeys = ReferenceEquals(keys, modelKeys) ? searchKeys : modelKeys;
            if (keys.Count(entry => entry.Id == key.Id) == 1 && !otherKeys.Contains(key))
            {
                await Task.Run(() => ApiKey.DeleteKey(key.Id));
                index = FindSelectedRow();
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
}
