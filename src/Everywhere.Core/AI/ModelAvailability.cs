using System.Globalization;

namespace Everywhere.AI;

/// <summary>
/// Describes the current availability status of a selected model.
/// </summary>
public enum ModelAvailabilityKind
{
    None,
    Unknown,
    Available,
    DeprecatingSoon,
    Deprecated,
    Unavailable
}

/// <summary>
/// Evaluates whether a selected model needs user attention.
/// </summary>
public sealed record ModelAvailability(ModelAvailabilityKind Kind, string? ModelId, DateOnly? DeprecationDate)
{
    private const int DeprecationWarningDays = 7;

    private static ModelAvailability None { get; } = new(ModelAvailabilityKind.None, null, null);

    public bool ShouldShowChatNotification =>
        Kind is ModelAvailabilityKind.Unavailable or ModelAvailabilityKind.DeprecatingSoon or ModelAvailabilityKind.Deprecated;

    public static ModelAvailability Evaluate(
        IModelDefinition selectedModel,
        IReadOnlyCollection<IModelDefinition> availableModels,
        DateOnly today)
    {
        if (selectedModel.ModelId.IsNullOrWhiteSpace()) return None;

        var deprecationDate = selectedModel.DeprecationDate;
        if (availableModels.Count > 0)
        {
            var availableModel = availableModels.FirstOrDefault(m => m.ModelId == selectedModel.ModelId);
            if (availableModel is null)
            {
                return new ModelAvailability(
                    ModelAvailabilityKind.Unavailable,
                    selectedModel.ModelId,
                    deprecationDate);
            }

            deprecationDate = availableModel.DeprecationDate;
        }

        if (deprecationDate is { } date)
        {
            if (date <= today)
            {
                return new ModelAvailability(ModelAvailabilityKind.Deprecated, selectedModel.ModelId, date);
            }

            if (date <= today.AddDays(DeprecationWarningDays))
            {
                return new ModelAvailability(ModelAvailabilityKind.DeprecatingSoon, selectedModel.ModelId, date);
            }
        }

        return new ModelAvailability(
            availableModels.Count == 0 ? ModelAvailabilityKind.Unknown : ModelAvailabilityKind.Available,
            selectedModel.ModelId,
            deprecationDate);
    }

    public string CreateDismissalKey(Guid assistantId, DateOnly localDate)
    {
        var deprecationDate = DeprecationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none";
        return string.Join(
            '|',
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            assistantId.ToString("N", CultureInfo.InvariantCulture),
            ModelId ?? string.Empty,
            Kind,
            deprecationDate);
    }
}
