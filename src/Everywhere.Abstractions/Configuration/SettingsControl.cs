using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Configuration;

/// <summary>
/// Creates settings controls using dependencies selected by their owner.
/// </summary>
public interface ISettingsControl
{
    /// <summary>
    /// Creates or retrieves a control for presentation on the UI thread.
    /// </summary>
    Control CreateControl();
}

/// <summary>
/// Represents a settings control associated with a specific type of control.
/// </summary>
/// <typeparam name="TControl">The control presented by the settings item.</typeparam>
public class SettingsControl<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TControl> : ISettingsControl
    where TControl : Control
{
    /// <summary>
    /// Gets or sets a value indicating whether the control is cached.
    /// </summary>
    /// <remarks>
    /// Disabling caching does not discard an instance already retained by this object.
    /// </remarks>
    public bool IsCacheEnabled { get; set; }

    private readonly Func<TControl>? _factory;

    private TControl? _control;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsControl{TControl}"/> class with the specified control instance.
    /// </summary>
    /// <param name="control">The existing instance to return on every presentation.</param>
    public SettingsControl(TControl control)
    {
        _control = control;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsControl{TControl}"/> class using the provided service provider.
    /// </summary>
    /// <remarks>
    /// The provider is retained but not disposed by this factory. Stop using the factory before the provider is disposed.
    /// Without caching, each presentation resolves the control again according to its service registration.
    /// </remarks>
    /// <param name="serviceProvider">The provider used to resolve the registered control.</param>
    /// <param name="isCacheEnabled">Whether to retain the first resolved control. Defaults to false.</param>
    public SettingsControl(IServiceProvider serviceProvider, bool isCacheEnabled = false)
    {
        _factory = serviceProvider.GetRequiredService<TControl>;
        IsCacheEnabled = isCacheEnabled;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsControl{TControl}"/> class with the specified factory function.
    /// </summary>
    /// <remarks>
    /// The delegate is retained. Its captured dependencies must remain available while the factory is in use.
    /// </remarks>
    /// <param name="factory">Creates a control when first presented, or on each presentation if caching is disabled.</param>
    /// <param name="isCacheEnabled">Whether to retain the first created control. Defaults to true.</param>
    public SettingsControl(Func<TControl> factory, bool isCacheEnabled = true)
    {
        _factory = factory;
        IsCacheEnabled = isCacheEnabled;
    }

    /// <inheritdoc />
    public Control CreateControl()
    {
        if (_control is not null)
        {
            return _control;
        }

        var control = _factory?.Invoke() ?? throw new InvalidOperationException($"Failed to create an instance of {typeof(TControl).FullName}.");
        if (IsCacheEnabled) _control = control;
        return control;
    }
}
