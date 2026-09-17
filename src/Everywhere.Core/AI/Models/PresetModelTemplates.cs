using System.Text.Json.Serialization;
using Everywhere.Configuration;

namespace Everywhere.AI;

public static class PresetModelTemplates
{
    /// <summary>
    /// Helper property to get all supported model provider templates.
    /// </summary>
    [JsonIgnore]
    [SettingsItemIgnore]
    public static ModelProviderTemplate[] Providers { get; } =
    [
        new()
        {
            Id = "openai",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_OpenAI),
            Endpoint = "https://api.openai.com/v1",
            OfficialWebsiteUrl = "https://openai.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/openai-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/openai-light.svg",
            Schema = ModelProviderSchema.OpenAIResponses,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.5",
                    Name = "GPT-5.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_050_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.4",
                    Name = "GPT-5.4",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_050_000,
                    OutputLimit = 128_000,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.4-mini",
                    Name = "GPT-5.4 mini",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.4-nano",
                    Name = "GPT-5.4 nano",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    OutputLimit = 128_000,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.2",
                    Name = "GPT-5.2",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.1",
                    Name = "GPT-5.1",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5",
                    Name = "GPT-5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5-mini",
                    Name = "GPT-5 mini",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5-nano",
                    Name = "GPT-5 nano",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "o4-mini",
                    Name = "o4-mini",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 100_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-4.1",
                    Name = "GPT 4.1",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_047_576,
                    OutputLimit = 32_768
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-4.1-mini",
                    Name = "GPT 4.1 mini",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_047_576,
                    OutputLimit = 32_768
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-4o",
                    Name = "GPT-4o",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 128_000,
                    OutputLimit = 16_384
                }
            ]
        },
        new()
        {
            Id = "anthropic",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_Anthropic),
            Endpoint = "https://api.anthropic.com",
            OfficialWebsiteUrl = "https://www.anthropic.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/anthropic-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/anthropic-light.svg",
            Schema = ModelProviderSchema.Anthropic,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-fable-5",
                    Name = "Claude Fable 5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-8",
                    Name = "Claude Opus 4.8",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-7",
                    Name = "Claude Opus 4.7",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-6",
                    Name = "Claude Opus 4.6",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-5",
                    Name = "Claude Opus 4.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-sonnet-4-6",
                    Name = "Claude Sonnet 4.6",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 64_000,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-sonnet-4-5",
                    Name = "Claude Sonnet 4.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-haiku-4-5",
                    Name = "Claude Haiku 4.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-1",
                    Name = "Claude Opus 4.1",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 32_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-0",
                    Name = "Claude Opus 4",
                    SupportsToolCall = true,
                    DeprecationDate = new DateOnly(2026, 6, 15),
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 32_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-sonnet-4-0",
                    Name = "Claude Sonnet 4",
                    SupportsToolCall = true,
                    DeprecationDate = new DateOnly(2026, 6, 15),
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000
                }
            ]
        },
        new()
        {
            Id = "google",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_Google),
            OfficialWebsiteUrl = "https://gemini.google.com",
            Endpoint = "https://generativelanguage.googleapis.com/v1beta",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/google-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/google-color.svg",
            Schema = ModelProviderSchema.Google,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-3.5-flash",
                    Name = "Gemini 3.5 Flash",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-3.1-pro-preview",
                    Name = "Gemini 3.1 Pro Preview",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-3-flash-preview",
                    Name = "Gemini 3 Flash Preview",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-2.5-pro",
                    Name = "Gemini 2.5 Pro",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-2.5-flash",
                    Name = "Gemini 2.5 Flash",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-3.1-flash-lite",
                    Name = "Gemini 3.1 Flash-Lite",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-2.5-flash-lite",
                    Name = "Gemini 2.5 Flash-Lite",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                }
            ]
        },
        new()
        {
            Id = "mistral",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_Mistral),
            OfficialWebsiteUrl = "https://mistral.ai",
            Endpoint = "https://api.mistral.ai/v1",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/mistral-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/mistral-color.svg",
            Schema = ModelProviderSchema.Mistral,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "mistral-medium-latest",
                    Name = "Mistral Medium",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 256_000,
                    OutputLimit = 128_000,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "mistral-small-latest",
                    Name = "Mistral Small",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 256_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "mistral-large-latest",
                    Name = "Mistral Large",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 256_000,
                    OutputLimit = 128_000
                }
            ]
        },
        new()
        {
            Id = "deepseek",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_DeepSeek),
            Endpoint = "https://api.deepseek.com",
            OfficialWebsiteUrl = "https://www.deepseek.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/deepseek-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/deepseek-color.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-v4-pro",
                    Name = "DeepSeek V4 Pro",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 384_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-v4-flash",
                    Name = "DeepSeek V4 Flash",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 384_000,
                    IsDefault = true,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                }
            ]
        },
        new()
        {
            Id = "moonshotai",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_MoonshotInternational),
            Endpoint = "https://api.moonshot.ai/v1",
            Schema = ModelProviderSchema.OpenAI,
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/moonshot-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/moonshot-light.svg",
            DocumentationUrl = "https://platform.moonshot.ai/docs/api/chat",
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2.6",
                    Name = "Kimi K2.6",
                    SupportsToolCall = true,
                    IsDefault = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262144,
                    OutputLimit = 262144
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2.7-code",
                    Name = "Kimi K2.7 Code",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262144,
                    OutputLimit = 262144,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2.7-code-highspeed",
                    Name = "Kimi K2.7 Code HighSpeed",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262144,
                    OutputLimit = 262144,
                    Specializations = ModelSpecializations.ImageUnderstanding
                },
            ]
        },
        new()
        {
            Id = "moonshotai-cn",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_MoonshotChina),
            Endpoint = "https://api.moonshot.cn/v1",
            OfficialWebsiteUrl = "https://www.moonshot.cn",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/moonshot-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/moonshot-light.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2.6",
                    Name = "Kimi K2.6",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2.5",
                    Name = "Kimi K2.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144
                }
            ]
        },
        new()
        {
            Id = "minimax",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_MiniMaxInternational),
            Endpoint = "https://api.minimax.io/anthropic",
            Schema = ModelProviderSchema.Anthropic,
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/minimax-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/minimax-color.svg",
            DocumentationUrl = "https://platform.minimax.io/docs/guides/quickstart",
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.7",
                    Name = "MiniMax-M2.7",
                    SupportsToolCall = true,
                    IsDefault = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204800,
                    OutputLimit = 131072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.5",
                    Name = "MiniMax-M2.5",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204800,
                    OutputLimit = 131072,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.1",
                    Name = "MiniMax-M2.1",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204800,
                    OutputLimit = 131072,
                    Specializations = ModelSpecializations.Default
                },
            ]
        },
        new()
        {
            Id = "minimax-cn",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_MiniMaxChina),
            Endpoint = "https://api.minimaxi.com/anthropic",
            OfficialWebsiteUrl = "https://minimaxi.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/minimax-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/minimax-color.svg",
            Schema = ModelProviderSchema.Anthropic,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M3",
                    Name = "MiniMax-M3",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.7",
                    Name = "MiniMax-M2.7",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.7-highspeed",
                    Name = "MiniMax-M2.7-highspeed",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.5",
                    Name = "MiniMax-M2.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.5-highspeed",
                    Name = "MiniMax-M2.5-highspeed",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.1",
                    Name = "MiniMax-M2.1",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.1-highspeed",
                    Name = "MiniMax-M2.1-highspeed",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2",
                    Name = "MiniMax-M2",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 196_608,
                    OutputLimit = 128_000
                },
            ]
        },
        new()
        {
            Id = "openrouter",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_OpenRouter),
            OfficialWebsiteUrl = "https://openrouter.ai",
            Endpoint = "https://openrouter.ai/api/v1",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/openrouter-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/openrouter-light.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                // According to 2026/03/31 Rankings
                new ModelDefinitionTemplate
                {
                    ModelId = "xiaomi/mimo-v2-pro",
                    Name = "Xiaomi: MiMo-V2-Pro",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 131_072,
                    IsDefault = true,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "stepfun/step-3.5-flash:free",
                    Name = "StepFun: Step 3.5 Flash (free)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 256_000,
                    OutputLimit = 256_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "minimax/minimax-m2.7",
                    Name = "MiniMax: MiniMax M2.7",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 196_608,
                    OutputLimit = 131_072,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek/deepseek-v3.2",
                    Name = "DeepSeek: DeepSeek V3.2",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_072,
                    OutputLimit = 65_536,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-sonnet-4.6",
                    Name = "Anthropic: Claude Sonnet 4.6",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-opus-4.6",
                    Name = "Anthropic: Claude Opus 4.6",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "google/gemini-3-flash-preview",
                    Name = "Google: Gemini 3 Flash Preview",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "z-ai/glm-5-turbo",
                    Name = "Z.ai: GLM 5 Turbo",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 202_752,
                    OutputLimit = 131_072,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "minimax/minimax-m2.5",
                    Name = "MiniMax: MiniMax M2.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 196_608,
                    OutputLimit = 196_608,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "x-ai/grok-4.1-fast",
                    Name = "X-AI: Grok 4.1 Fast",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 2_000_000,
                    OutputLimit = 30_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "google/gemini-2.5-flash",
                    Name = "Google: Gemini 2.5 Flash",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_535,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-opus-4.5",
                    Name = "Anthropic: Claude Opus 4.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-sonnet-4.5",
                    Name = "Anthropic: Claude Sonnet 4.5",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 64_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "openai/gpt-oss-120b",
                    Name = "OpenAI: GPT-OSS 120B",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_072,
                    OutputLimit = 32_768,
                }
            ]
        },
        new()
        {
            Id = "siliconflow",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_SiliconFlowInternational),
            Endpoint = "https://api.siliconflow.com/v1",
            Schema = ModelProviderSchema.OpenAI,
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/siliconcloud-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/siliconcloud-color.svg",
            DocumentationUrl = "https://cloud.siliconflow.com/models",
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "Qwen/Qwen3-8B",
                    Name = "Qwen/Qwen3-8B",
                    SupportsToolCall = true,
                    IsDefault = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131000,
                    OutputLimit = 131000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Qwen/Qwen3.5-35B-A3B",
                    Name = "Qwen3.5 35B-A3B",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262144,
                    OutputLimit = 262144,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Qwen/Qwen3.5-397B-A17B",
                    Name = "Qwen3.5 397B-A17B",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262144,
                    OutputLimit = 262144,
                    Specializations = ModelSpecializations.Default
                },
            ]
        },
        new()
        {
            Id = "siliconflow-cn",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_SiliconFlowChina),
            OfficialWebsiteUrl = "https://www.siliconflow.cn",
            Endpoint = "https://api.siliconflow.cn/v1",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/siliconcloud-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/siliconcloud-color.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                // According to 2026/03/31 Models
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/MiniMaxAI/MiniMax-M2.5",
                    Name = "MiniMax-M2.5 (Pro)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 192_000,
                    OutputLimit = 131_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/zai-org/GLM-5",
                    Name = "GLM-5 (Pro)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 205_000,
                    OutputLimit = 205_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/moonshotai/Kimi-K2.5",
                    Name = "Kimi-K2.5 (Pro)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_000,
                    OutputLimit = 262_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/zai-org/GLM-4.7",
                    Name = "GLM-4.7 (Pro)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 205_000,
                    OutputLimit = 205_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-ai/DeepSeek-V3.2",
                    Name = "DeepSeek-V3.2",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 164_000,
                    OutputLimit = 164_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/deepseek-ai/DeepSeek-V3.2",
                    Name = "DeepSeek-V3.2 (Pro)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 164_000,
                    OutputLimit = 164_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Qwen/Qwen3-8B",
                    Name = "Qwen3-8B (free)",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_000,
                    OutputLimit = 131_000,
                    IsDefault = true,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "zai-org/GLM-4.6V",
                    Name = "GLM 4.6V",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_000,
                    OutputLimit = 131_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "moonshotai/Kimi-K2-Thinking",
                    Name = "Kimi K2 Thinking",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_000,
                    OutputLimit = 262_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMaxAI/MiniMax-M2.1",
                    Name = "MiniMax M2.1",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 197_000,
                    OutputLimit = 131_000,
                }
            ]
        },
        new()
        {
            Id = "zai",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_ZhipuInternational),
            Endpoint = "https://api.z.ai/api/paas/v4",
            Schema = ModelProviderSchema.OpenAI,
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/zai-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/zai-light.svg",
            DocumentationUrl = "https://docs.z.ai/guides/overview/pricing",
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "glm-4.7",
                    Name = "GLM-4.7",
                    SupportsToolCall = true,
                    IsDefault = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204800,
                    OutputLimit = 131072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "glm-4.7-flash",
                    Name = "GLM-4.7-Flash",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200000,
                    OutputLimit = 131072,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "glm-4.6v",
                    Name = "GLM-4.6V",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 128000,
                    OutputLimit = 32768,
                    Specializations = ModelSpecializations.ImageUnderstanding
                },
            ]
        },
        new()
        {
            Id = "zhipuai",
            DisplayNameKey = new DynamicLocaleKey(LocaleKey.PresetModelProvider_ZhipuChina),
            Endpoint = "https://open.bigmodel.cn/api/paas/v4",
            Schema = ModelProviderSchema.OpenAI,
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/zai-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/zai-light.svg",
            DocumentationUrl = "https://docs.z.ai/guides/overview/pricing",
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "glm-4.7",
                    Name = "GLM-4.7",
                    SupportsToolCall = true,
                    IsDefault = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204800,
                    OutputLimit = 131072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "glm-4.7-flash",
                    Name = "GLM-4.7-Flash",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200000,
                    OutputLimit = 131072,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "glm-4.6v",
                    Name = "GLM-4.6V",
                    SupportsToolCall = true,
                    IsDefault = false,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 128000,
                    OutputLimit = 32768,
                    Specializations = ModelSpecializations.ImageUnderstanding
                },
            ]
        }
    ];
}