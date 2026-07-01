using Microsoft.CodeAnalysis;

namespace Everywhere.Configuration.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = $"{nameof(Everywhere)}.{nameof(Configuration)}.{nameof(SourceGenerator)}";

    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "STG001",
        title: "Target class must be partial",
        messageFormat: "Class '{0}' is marked with [GeneratedSettingsItems] but is not partial",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NullableSettingsControl = new(
        id: "STG003",
        title: "Nullable SettingsControl",
        messageFormat: "Property '{0}' is of type SettingsControl<T>? but must be non-nullable",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingItemsSourceBindingPath = new(
        id: "STG004",
        title: "Missing ItemsSource Binding Path",
        messageFormat: "Property '{0}' is a collection but has no ItemsSourceBindingPath specified in [SettingsItemsSource]",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidItemsSourceBindingPath = new(
        id: "STG005",
        title: "Invalid ItemsSource Binding Path",
        messageFormat: "Property '{0}' has an invalid ItemsSourceBindingPath '{1}' specified in [SettingsItemsSource]",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedSettingsItemType = new(
        id: "STG006",
        title: "Unsupported Settings Item Type, using implicit TemplatedItem",
        messageFormat: "Property '{0}' is of unsupported type '{1}' for a settings item, it's recommended to use explicit SettingsTemplatedItem attribute",
        category: Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidSettingsItemsType = new(
        id: "STG007",
        title: "Invalid SettingsItems Type",
        messageFormat: "Property '{0}' is marked with [SettingsItems] but does not have a property of 'IEnumerable<SettingsItem> SettingsItems' type",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}