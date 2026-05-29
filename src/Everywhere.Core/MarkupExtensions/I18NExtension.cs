using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Disposables;
using Avalonia.Collections;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using ZLinq;

namespace Everywhere.MarkupExtensions;

[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.PublicFields |
    DynamicallyAccessedMemberTypes.PublicProperties)]
public class I18NExtension : MarkupExtension
{
    [AssignBinding]
    public required object Key { get; set; }

    [Content, AssignBinding]
    public AvaloniaList<object> Arguments { get; set; } = [];

    public IValueConverter? Converter { get; set; }

    public object? ConverterParameter { get; set; }

    public CultureInfo? ConverterCulture { get; set; }

    /// <summary>
    /// Whether to resolve the resource key immediately. If true, the extension will return the resolved value directly.
    /// If false, it will return a binding that resolves the value at runtime.
    /// </summary>
    public bool Resolve { get; set; }

    public I18NExtension() { }

    [SetsRequiredMembers]
    public I18NExtension(object key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var target = serviceProvider.GetService<IProvideValueTarget>();

        if (Key is BindingBase binding)
        {
            return new MultiBinding
            {
                Bindings = [binding],
                Converter = Resolve ? null : new BindingResolver(target) // only use BindingResolver when not resolving immediately
            };
        }

        var dynamicResourceKey = Key switch
        {
            IDynamicResourceKey key => key,
            _ when Arguments is { Count: > 0 } args => new FormattedDynamicResourceKey(
                Key,
                args.AsValueEnumerable().Select(arg => arg switch
                {
                    BindingBase b => new BindingResourceKey(b, target?.TargetObject as AvaloniaObject, target?.TargetProperty as AvaloniaProperty),
                    IDynamicResourceKey key => key,
                    _ => new DynamicResourceKey(arg)
                }).ToList()),
            _ => new DynamicResourceKey(Key)
        };
        return Resolve ?
            dynamicResourceKey.ToString() ?? string.Empty :
            new Binding
            {
                Path = $"{nameof(IDynamicResourceKey.Self)}^",
                Source = dynamicResourceKey,
                Converter = Converter,
                ConverterParameter = ConverterParameter,
                ConverterCulture = ConverterCulture,
            };
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
            if (values is not [IDynamicResourceKey key]) return null;

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

    /// <summary>
    /// This class is used to create a dynamic resource key for axaml Binding.
    /// </summary>
    /// <param name="binding"></param>
    /// <param name="target"></param>
    /// <param name="property"></param>
    private sealed class BindingResourceKey(
        BindingBase binding,
        AvaloniaObject? target,
        AvaloniaProperty? property
    ) : IDynamicResourceKey, IObserver<object?>
    {
        public IDynamicResourceKey Self => this;

// #pragma warning disable CS0618
//         private readonly BindingExpressionBase? _bindingInstance = binding.Initiate(target ?? new AvaloniaObject(), property);
// #pragma warning restore CS0618

        private IDisposable? _selfSubscription;
        private object? _value;

        public IDisposable Subscribe(IObserver<object?> observer)
        {
            return Disposable.Empty;
            // DisposeHelper.DisposeToDefault(ref _selfSubscription);
            // _selfSubscription = _bindingInstance?.Source.Subscribe(this); // Subscribe to the binding so that we can get updates.
            //
            // return _bindingInstance?.Source.Subscribe(observer) ?? Disposable.Empty;
        }

        public override string ToString() => _value?.ToString() ?? string.Empty;

        public void OnCompleted() { }

        public void OnError(Exception error) => _value = null;

        public void OnNext(object? value) => _value = value;
    }
}