using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Avalonia.PropertyStore;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.I18N;

/// <summary>
/// Resolves a locale key in XAML and keeps the displayed text in sync with the locale and its arguments.
/// </summary>
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.PublicFields |
    DynamicallyAccessedMemberTypes.PublicProperties)]
public class I18NExtension : MarkupExtension
{
    /// <summary>
    /// The resource key, dynamic locale key, or binding that supplies the key.
    /// </summary>
    [AssignBinding]
    public required object Key { get; set; }

    /// <summary>
    /// Values to substitute into the localized format string.
    /// </summary>
    [Content, AssignBinding]
    public AvaloniaList<object> Arguments
    {
        get => _arguments ??= [];
        set => _arguments = value;
    }

    /// <summary>
    /// Whether to resolve the resource key immediately. If true, the extension will return the resolved value directly.
    /// If false, it will return a binding that resolves the value at runtime.
    /// </summary>
    public bool Resolve { get; set; }

    private AvaloniaList<object>? _arguments;

    public I18NExtension() { }

    [SetsRequiredMembers]
    public I18NExtension(object key) => Key = key;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (Key is BindingBase binding)
        {
            return new MultiBinding
            {
                Bindings = [binding],
                Converter = Resolve ? null : new BindingResolver(serviceProvider.GetService<IProvideValueTarget>())
            };
        }

        if (Key is IDynamicLocaleKey key) return Resolve ? key.ToString() ?? string.Empty : key.ToBinding();

        if (Resolve)
        {
            var format = DynamicLocaleKey.Resolve(Key);
            if (_arguments is not { Count: > 0 } args) return format;

            // A snapshot has no live target; binding arguments have not produced a value.
            var values = new object?[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] is not BindingBase) values[i] = ResolveArgument(args[i]);
            }

            return Format(format, values);
        }

        return new I18NBinding(Key, _arguments is { Count: > 0 } arguments ? [.. arguments] : []);
    }

    private static string? ResolveArgument(object argument)
    {
        return argument is IDynamicLocaleKey key ? key.ToString() : DynamicLocaleKey.Resolve(argument);
    }

    private static string Format(string format, object?[] values)
    {
        try
        {
            return values.Length switch
            {
                0 => format,
                1 => string.Format(format, values[0]),
                2 => string.Format(format, values[0], values[1]),
                3 => string.Format(format, values[0], values[1], values[2]),
                _ => string.Format(format, values)
            };
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"Failed to format locale string '{format}': {e.Message}");
            return format;
        }
    }

    private sealed class BindingResolver : IObserver<object?>, IMultiValueConverter
    {
        private readonly WeakReference<object>? _targetObject;
        private readonly WeakReference<object>? _targetProperty;
        private IDisposable? _subscription;

        public BindingResolver(IProvideValueTarget? target)
        {
            if (target is null) return;
            _targetObject = new WeakReference<object>(target.TargetObject);
            _targetProperty = new WeakReference<object>(target.TargetProperty);
        }

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            _subscription?.Dispose();
            if (values is not [IDynamicLocaleKey key]) return null;

            _subscription = key.Subscribe(this);
            return key.ToString(); // return resolved string immediately. If it changes, OnNext will be called to update the target.
        }

        public void OnNext(object? value)
        {
            if (_targetObject?.TryGetTarget(out var targetObject) is not true) return;
            if (_targetProperty?.TryGetTarget(out var targetProperty) is not true) return;

            if (targetProperty is not IPropertyInfo { CanSet: true } propertyInfo) return;
            propertyInfo.Set(targetObject, value);
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }

    private sealed class I18NBinding(object key, object[] arguments) : BindingBase
    {
        public override BindingExpressionBase CreateInstance(AvaloniaObject target, AvaloniaProperty? targetProperty, object? anchor)
        {
            return new I18NBindingExpression(key, arguments);
        }
    }

    // Internal so CommunityToolkit.Mvvm's IMessengerRegisterAllGenerator can register IRecipient<LocaleChangedMessage>.
    internal sealed class I18NBindingExpression(object key, object[] arguments) :
        UntypedBindingExpressionBase(BindingPriority.LocalValue),
        IBindingExpressionSink,
        IRecipient<LocaleChangedMessage>,
        IObserver<object?>
    {
        public override string Description => "I18N";

        // Child expressions need target metadata, not an entry in the control's value store.
        private static readonly StyledProperty<object?> ArgTargetProperty = AvaloniaProperty.Register<AvaloniaObject, object?>(
            "I18NArg",
            defaultBindingMode: BindingMode.OneWay,
            enableDataValidation: false);

        private readonly IDisposable?[] _subscriptions = arguments.Length == 0 ? [] : new IDisposable?[arguments.Length];
        private readonly object?[] _values = arguments.Length == 0 ? [] : new object?[arguments.Length];
        private string _format = string.Empty;
        private bool _isStarting;

        protected override void StartCore()
        {
            if (!TryGetTarget(out var target)) throw new AvaloniaInternalException("I18N binding has no target.");

            WeakReferenceMessenger.Default.Register(this);
            _isStarting = true;
            try
            {
                for (var i = 0; i < arguments.Length; i++)
                {
                    switch (arguments[i])
                    {
                        case BindingBase binding:
                            var expression = binding.CreateInstance(target, ArgTargetProperty, null);
                            _subscriptions[i] = expression;
                            expression.Attach(this, null, target, ArgTargetProperty, Priority);
                            expression.Start(true);
                            break;
                        case IDynamicLocaleKey dynamicKey:
                            _subscriptions[i] = dynamicKey.Subscribe(this);
                            break;
                    }
                }
            }
            finally
            {
                _isStarting = false;
            }

            RefreshLocale();
        }

        protected override void StopCore()
        {
            WeakReferenceMessenger.Default.Unregister<LocaleChangedMessage>(this);
            for (var i = 0; i < _subscriptions.Length; i++)
            {
                _subscriptions[i]?.Dispose();
                _subscriptions[i] = null;
                _values[i] = null;
            }
            _format = string.Empty;
        }

        void IBindingExpressionSink.OnChanged(BindingExpressionBase instance, bool hasValueChanged, bool hasErrorChanged)
        {
            if (!hasValueChanged) return;

            // A background source may have queued a notification before its expression was detached.
            var index = Array.IndexOf(_subscriptions, instance);
            if (index < 0) return;

            IValueEntry entry = instance;
            var value = !entry.HasValue() ? null : entry is IValueEntry<string?> text ? text.GetValue() : entry.GetValue();
            if (value == BindingOperations.DoNothing) return;

            _values[index] = value is UnsetValueType or BindingNotification ? null : value?.ToString();
            if (!_isStarting) PublishValue(Format(_format, _values));
        }

        void IBindingExpressionSink.OnCompleted(BindingExpressionBase instance) { }

        public void Receive(LocaleChangedMessage message) => ScheduleRefreshLocale();

        void IObserver<object?>.OnNext(object? value) => ScheduleRefreshLocale();
        void IObserver<object?>.OnCompleted() { }
        void IObserver<object?>.OnError(Exception error) { }

        private void ScheduleRefreshLocale()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                RefreshLocale();
            }
            else
            {
                Dispatcher.UIThread.Post(RefreshLocale);
            }
        }

        private void RefreshLocale()
        {
            if (!IsRunning || _isStarting) return;

            _format = DynamicLocaleKey.Resolve(key);
            for (var i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] is not BindingBase) _values[i] = ResolveArgument(arguments[i]);
            }

            PublishValue(Format(_format, _values));
        }
    }
}
