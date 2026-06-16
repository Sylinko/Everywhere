using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using Avalonia.Collections;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
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
            IDynamicLocaleKey key => key,
            _ when Arguments is { Count: > 0 } args => new FormattedDynamicLocaleKey(
                Key,
                args.AsValueEnumerable().Select(arg => arg switch
                {
                    BindingBase b => new BindingLocaleKey(b, target?.TargetObject as AvaloniaObject, target?.TargetProperty as AvaloniaProperty),
                    IDynamicLocaleKey key => key,
                    _ => new DynamicLocaleKey(arg)
                }).ToList()),
            _ => new DynamicLocaleKey(Key)
        };
        return Resolve ? dynamicResourceKey.ToString() ?? string.Empty : dynamicResourceKey.ToBinding();
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

    /// <summary>
    /// This class is used to create a dynamic resource key for axaml Binding.
    /// </summary>
    /// <param name="binding"></param>
    /// <param name="target"></param>
    /// <param name="property"></param>
    private sealed class BindingLocaleKey(BindingBase binding, AvaloniaObject? target, AvaloniaProperty? property) : IDynamicLocaleKey
    {
        public IDisposable Subscribe(IObserver<object?> observer)
        {
            if (CreateInstance(binding, target ?? new AvaloniaObject(), property, null) is not UntypedBindingExpressionBase expression ||
                ToObservable(expression) is not IObservable<object?> observable)
            {
                return Disposable.Empty;
            }

            return observable.Subscribe(new AnonymousObserver<object?>(value => observer.OnNext(value switch
            {
                BindingNotification => null,
                _ => value?.ToString()
            })));
        }

        // internal abstract BindingExpressionBase CreateInstance(
        //     AvaloniaObject target,
        //     AvaloniaProperty? targetProperty,
        //     object? anchor);
        [UnsafeAccessor(UnsafeAccessorKind.Method)]
        private extern static BindingExpressionBase CreateInstance(
            BindingBase binding,
            AvaloniaObject target,
            AvaloniaProperty? property,
            object? anchor);

        // internal IAvaloniaSubject<object?> ToObservable(AvaloniaObject? target = null)
        [UnsafeAccessor(UnsafeAccessorKind.Method)]
        [return: UnsafeAccessorType("Avalonia.Reactive.IAvaloniaSubject`1[[System.Object, System.Private.CoreLib]], Avalonia.Base")]
        private extern static object ToObservable(
            UntypedBindingExpressionBase binding,
            AvaloniaObject? target = null);
    }
}