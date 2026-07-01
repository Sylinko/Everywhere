using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Everywhere.Common;

namespace Everywhere.Configuration;

public interface ISettingsControl
{
    Control CreateControl();
}

/// <summary>
/// Represents a settings control associated with a specific type of control.
/// </summary>
/// <typeparam name="TControl"></typeparam>
public class SettingsControl<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TControl> : ISettingsControl
    where TControl : Control
{
    public bool IsCacheEnabled { get; set; }

    private readonly Func<IServiceProvider, TControl>? _factory;

    private TControl? _control;

    public SettingsControl() { }

    public SettingsControl(TControl control)
    {
        _control = control;
    }

    public SettingsControl(Func<IServiceProvider, TControl> factory, bool isCacheEnabled = true)
    {
        _factory = factory;
        IsCacheEnabled = isCacheEnabled;
    }

    public Control CreateControl()
    {
        if (_control is not null)
        {
            return _control;
        }

        var control = _factory is not null ?
            _factory(ServiceLocator.Resolve<IServiceProvider>()) :
            ServiceLocator.Resolve<TControl>();
        if (IsCacheEnabled) _control = control;
        return control;
    }
}