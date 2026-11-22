using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.NUnit;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Utilities;

namespace Everywhere.Core.Tests.Views;

/// <summary>
/// Verifies that collectible mod assemblies interacting with <c>Avalonia.Base</c> and <c>Avalonia.Controls</c>
/// (<c>Sylinko/Avalonia</c>) APIs unload cleanly without leaking strong GC roots after sweeping.
/// </summary>
[TestFixture]
public class AvaloniaCollectibleUnloadTests
{
    private sealed class TestModLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    [AvaloniaTest]
    public void StyledAttachedDirectAndMetadataOverrides_AfterVisualAttachAndSweep_CollectsAlc()
    {
        var setup = AttachPropertyAndStylingScenario();

        try
        {
            SweepAndUnload(setup.WeakAlc);
            AssertAlcCollected(setup.WeakAlc);

            // Surviving host controls must remain functional after mod metadata and values are swept.
            setup.HostBorder.Tag = "host-still-works";
            Assert.That(setup.HostBorder.Tag, Is.EqualTo("host-still-works"));
            Assert.That(setup.HostPanel.Children, Is.EqualTo(new Control[] { setup.HostBorder }));
            Assert.That(setup.HostWindow.IsVisible, Is.True);
        }
        finally
        {
            setup.HostWindow.Close();
        }
    }

    [AvaloniaTest]
    public void RoutedEventsWeakEventsInputAndAssetCaches_AfterSweep_CollectsAlc()
    {
        var setup = AttachEventAndCacheScenario();

        try
        {
            SweepAndUnload(setup.WeakAlc);
            AssertAlcCollected(setup.WeakAlc);

            // Surviving host controls and event routes remain usable after cleanup.
            setup.HostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(setup.HostWindow.IsVisible, Is.True);
        }
        finally
        {
            setup.HostWindow.Close();
            AssetLoader.SetDefaultAssembly(typeof(AvaloniaCollectibleUnloadTests).Assembly);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<AssemblyLoadContext> WeakAlc, Window HostWindow, Border HostBorder, StackPanel HostPanel)
        AttachPropertyAndStylingScenario()
    {
        var alc = new TestModLoadContext();
        var weakAlc = new WeakReference<AssemblyLoadContext>(alc);
        var assembly = LoadDynamicModAssembly(alc, $"ModPropTest_{Guid.NewGuid():N}");

        var modValueType = assembly.GetType("ModDynamic.ModValue", throwOnError: true)!;
        var modControlType = assembly.GetType("ModDynamic.ModControl", throwOnError: true)!;
        var modHandlersType = assembly.GetType("ModDynamic.ModHandlers", throwOnError: true)!;

        var styledProperty = RegisterModStyledProperty(modControlType, modValueType);
        var validatedIntProperty = RegisterModValidatedStyledProperty(modControlType, modHandlersType);
        var attachedProperty = RegisterModAttachedProperty(modControlType, modValueType);
        var directProperty = RegisterModDirectProperty(modControlType, modHandlersType);

        OverrideBorderTagMetadata(modHandlersType, modValueType);
        SubscribeBorderTagObserver(modHandlersType);

        var modControl = (Control)Activator.CreateInstance(modControlType)!;
        var modValue = Activator.CreateInstance(modValueType)!;
        modControl.SetValue(styledProperty, modValue);
        modControl.SetValue(validatedIntProperty, 5);
        modControl.SetValue(directProperty, 9);

        var hostBorder = new Border();
        hostBorder.SetValue(attachedProperty, modValue);

        var hostPanel = new StackPanel
        {
            Children = { hostBorder, modControl }
        };

        var app = Application.Current;
        if (app is not null)
        {
            var modStyle = new Style(selector => selector.OfType(modControlType));
            modStyle.Setters.Add(new Setter(styledProperty, modValue));
            app.Styles.Add(modStyle);

            var modTheme = new ControlTheme(modControlType);
            app.Resources[modControlType] = modTheme;
            app.DataTemplates.Add(new FuncDataTemplate(modValueType, (_, _) => new Border()));
        }

        var hostWindow = new Window
        {
            Content = hostPanel
        };
        hostWindow.Show();
        Dispatcher.UIThread.RunJobs();

        return (weakAlc, hostWindow, hostBorder, hostPanel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<AssemblyLoadContext> WeakAlc, Window HostWindow, Button HostButton)
        AttachEventAndCacheScenario()
    {
        var alc = new TestModLoadContext();
        var weakAlc = new WeakReference<AssemblyLoadContext>(alc);
        var assemblyName = $"ModEventTest_{Guid.NewGuid():N}";
        var assembly = LoadDynamicModAssembly(alc, assemblyName);

        var modControlType = assembly.GetType("ModDynamic.ModControl", throwOnError: true)!;
        var modHandlersType = assembly.GetType("ModDynamic.ModHandlers", throwOnError: true)!;

        var registerMethod = typeof(RoutedEvent)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(RoutedEvent.Register) &&
                m.GetGenericArguments().Length == 2 &&
                m.GetParameters().Length == 2)
            .MakeGenericMethod(modControlType, typeof(RoutedEventArgs));
        _ = (RoutedEvent)registerMethod.Invoke(null, ["ModPing", RoutingStrategies.Bubble])!;

        var addGenericClassHandler = typeof(RoutedEvent<RoutedEventArgs>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == nameof(RoutedEvent<RoutedEventArgs>.AddClassHandler) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(Button));
        var classActionType = typeof(Action<Button, RoutedEventArgs>);
        var classHandler = modHandlersType.GetMethod("OnRoutedClass", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(classActionType);
        _ = addGenericClassHandler.Invoke(Button.ClickEvent, [classHandler, RoutingStrategies.Bubble, false]);

        var hostButton = new Button();
        var instanceHandler = (EventHandler<RoutedEventArgs>)modHandlersType
            .GetMethod("OnRoutedInstance", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(EventHandler<RoutedEventArgs>));
        hostButton.AddHandler(Button.ClickEvent, instanceHandler);

        var modControl = (Control)Activator.CreateInstance(modControlType)!;
        var weakHandler = (EventHandler<RoutedEventArgs>)modControlType
            .GetMethod("OnWeakRoutedEvent", BindingFlags.Public | BindingFlags.Instance)!
            .CreateDelegate(typeof(EventHandler<RoutedEventArgs>), modControl);
        var weakSubscribeMethod = typeof(WeakEventHandlerManager)
            .GetMethod(nameof(WeakEventHandlerManager.Subscribe), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Button), typeof(RoutedEventArgs), modControlType);
        weakSubscribeMethod.Invoke(null, [hostButton, nameof(Button.Click), weakHandler]);

        if (Application.Current is { } app)
        {
            var resHandler = (EventHandler<ResourcesChangedEventArgs>)modHandlersType
                .GetMethod("OnResourcesChanged", BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate(typeof(EventHandler<ResourcesChangedEventArgs>));
            app.ResourcesChanged += resHandler;
        }

        // Subscribe to internal InputManager.Process using AnonymousObserver<RawInputEventArgs>.
        var inputManagerType = typeof(AvaloniaObject).Assembly.GetType("Avalonia.Input.InputManager", throwOnError: true)!;
        var inputManagerInstance = inputManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (inputManagerInstance is not null)
        {
            var processObservable = (IObservable<RawInputEventArgs>)inputManagerInstance
                .GetType()
                .GetProperty("Process", BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(inputManagerInstance)!;
            var rawInputDelegate = (Action<RawInputEventArgs>)modHandlersType
                .GetMethod("OnRawInput", BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate(typeof(Action<RawInputEventArgs>));
            processObservable.Subscribe(new AnonymousObserver<RawInputEventArgs>(rawInputDelegate));
        }

        // Populate StandardAssetLoader's default resm assembly and FontManager's font collection cache.
        AssetLoader.SetDefaultAssembly(assembly);
        var fontKey = new Uri($"fonts:{assemblyName}");
        var fontSource = new Uri($"avares://{assemblyName}/Fonts");
        FontManager.Current.AddFontCollection(new EmbeddedFontCollection(fontKey, fontSource));

        var hostWindow = new Window
        {
            Content = new StackPanel
            {
                Children = { hostButton, modControl }
            }
        };
        hostWindow.Show();
        Dispatcher.UIThread.RunJobs();

        return (weakAlc, hostWindow, hostButton);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SweepAndUnload(WeakReference<AssemblyLoadContext> weakAlc)
    {
        if (!weakAlc.TryGetTarget(out var alc))
        {
            return;
        }

        IReadOnlySet<AssemblyLoadContext> retiring = new HashSet<AssemblyLoadContext> { alc };
        CollectibleAssemblyCleanup.SweepUI(retiring);
        CollectibleAssemblyCleanup.Sweep(retiring);
        alc.Unload();
    }

    private static void AssertAlcCollected(WeakReference<AssemblyLoadContext> weakAlc)
    {
        for (var i = 0; i < 20 && IsAlcAlive(weakAlc); i++)
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.That(IsAlcAlive(weakAlc), Is.False, "Collectible Avalonia mod ALC remained rooted after sweep and GC.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlcAlive(WeakReference<AssemblyLoadContext> weakAlc) => weakAlc.TryGetTarget(out _);

    private static AvaloniaProperty RegisterModStyledProperty(Type modControlType, Type modValueType)
    {
        var method = typeof(AvaloniaProperty)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(AvaloniaProperty.Register) &&
                m.GetGenericArguments().Length == 2 &&
                m.GetParameters().Length == 7)
            .MakeGenericMethod(modControlType, modValueType);
        return (AvaloniaProperty)method.Invoke(
            null,
            ["ModStyled", null, false, BindingMode.OneWay, null, null, false])!;
    }

    private static AvaloniaProperty RegisterModValidatedStyledProperty(Type modControlType, Type modHandlersType)
    {
        var method = typeof(AvaloniaProperty)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(AvaloniaProperty.Register) &&
                m.GetGenericArguments().Length == 2 &&
                m.GetParameters().Length == 7)
            .MakeGenericMethod(modControlType, typeof(int));

        var validate = modHandlersType.GetMethod("ValidateInt", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<int, bool>));
        var coerce = modHandlersType.GetMethod("CoerceInt", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<AvaloniaObject, int, int>));

        return (AvaloniaProperty)method.Invoke(
            null,
            ["ModValidated", 1, false, BindingMode.OneWay, validate, coerce, false])!;
    }

    private static AvaloniaProperty RegisterModAttachedProperty(Type modControlType, Type modValueType)
    {
        var method = typeof(AvaloniaProperty)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(AvaloniaProperty.RegisterAttached) &&
                m.GetGenericArguments().Length == 3 &&
                m.GetParameters().Length == 6)
            .MakeGenericMethod(modControlType, typeof(Border), modValueType);
        return (AvaloniaProperty)method.Invoke(
            null,
            ["ModAttached", null, false, BindingMode.OneWay, null, null])!;
    }

    private static AvaloniaProperty RegisterModDirectProperty(Type modControlType, Type modHandlersType)
    {
        var method = typeof(AvaloniaProperty)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(AvaloniaProperty.RegisterDirect) &&
                m.GetGenericArguments().Length == 2 &&
                m.GetParameters().Length == 6)
            .MakeGenericMethod(modControlType, typeof(int));

        var getter = modHandlersType.GetMethod("GetDirect", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<,>).MakeGenericType(modControlType, typeof(int)));
        var setter = modHandlersType.GetMethod("SetDirect", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Action<,>).MakeGenericType(modControlType, typeof(int)));

        return (AvaloniaProperty)method.Invoke(
            null,
            ["ModDirect", getter, setter, 0, BindingMode.TwoWay, false])!;
    }

    private static void OverrideBorderTagMetadata(Type modHandlersType, Type modValueType)
    {
        var defaultModValue = Activator.CreateInstance(modValueType);
        var overrideMetadata = typeof(StyledProperty<object?>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(StyledProperty<object?>.OverrideMetadata) &&
                m.GetGenericArguments().Length == 1 &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(StyledPropertyMetadata<>))
            .MakeGenericMethod(typeof(Border));

        var metadata = new StyledPropertyMetadata<object?>(new Optional<object?>(defaultModValue));
        overrideMetadata.Invoke(Control.TagProperty, [metadata]);

        var addClassHandler = typeof(AvaloniaObject)
            .Assembly
            .GetType("Avalonia.AvaloniaObjectExtensions", throwOnError: true)!
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(AvaloniaObjectExtensions.AddClassHandler) &&
                m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 &&
                m.GetParameters().Length == 2);
        var borderAddClassHandler = addClassHandler.MakeGenericMethod(typeof(Border));
        var classHandler = modHandlersType.GetMethod("OnTagChanged", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Action<Border, AvaloniaPropertyChangedEventArgs>));
        borderAddClassHandler.Invoke(null, [Control.TagProperty.Changed, classHandler]);
    }

    private static void SubscribeBorderTagObserver(Type modHandlersType)
    {
        var onObserved = (Action<AvaloniaPropertyChangedEventArgs>)modHandlersType
            .GetMethod("OnObserved", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Action<AvaloniaPropertyChangedEventArgs>));
        _ = Control.TagProperty.Changed.Subscribe(new AnonymousObserver<AvaloniaPropertyChangedEventArgs>(onObserved));
    }

    private static Assembly LoadDynamicModAssembly(AssemblyLoadContext alc, string assemblyName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var modValueBuilder = mb.DefineType("ModDynamic.ModValue", TypeAttributes.Public | TypeAttributes.Class);
        modValueBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        _ = modValueBuilder.CreateType();

        var modControlBuilder = mb.DefineType("ModDynamic.ModControl", TypeAttributes.Public | TypeAttributes.Class, typeof(Control));
        var controlCtor = modControlBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var controlCtorIl = controlCtor.GetILGenerator();
        controlCtorIl.Emit(OpCodes.Ldarg_0);
        controlCtorIl.Emit(OpCodes.Call, typeof(Control).GetConstructor(Type.EmptyTypes)!);
        controlCtorIl.Emit(OpCodes.Ret);

        var weakRouted = modControlBuilder.DefineMethod(
            "OnWeakRoutedEvent",
            MethodAttributes.Public,
            typeof(void),
            [typeof(object), typeof(RoutedEventArgs)]);
        weakRouted.GetILGenerator().Emit(OpCodes.Ret);
        var modControlType = modControlBuilder.CreateType();

        var handlersBuilder = mb.DefineType("ModDynamic.ModHandlers", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var validate = handlersBuilder.DefineMethod(
            "ValidateInt",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool),
            [typeof(int)]);
        var validateIl = validate.GetILGenerator();
        validateIl.Emit(OpCodes.Ldc_I4_1);
        validateIl.Emit(OpCodes.Ret);

        var coerce = handlersBuilder.DefineMethod(
            "CoerceInt",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            [typeof(AvaloniaObject), typeof(int)]);
        var coerceIl = coerce.GetILGenerator();
        coerceIl.Emit(OpCodes.Ldarg_1);
        coerceIl.Emit(OpCodes.Ret);

        var getDirect = handlersBuilder.DefineMethod(
            "GetDirect",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            [modControlType]);
        var getDirectIl = getDirect.GetILGenerator();
        getDirectIl.Emit(OpCodes.Ldc_I4_7);
        getDirectIl.Emit(OpCodes.Ret);

        var setDirect = handlersBuilder.DefineMethod(
            "SetDirect",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [modControlType, typeof(int)]);
        setDirect.GetILGenerator().Emit(OpCodes.Ret);

        var onTagChanged = handlersBuilder.DefineMethod(
            "OnTagChanged",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(Border), typeof(AvaloniaPropertyChangedEventArgs)]);
        onTagChanged.GetILGenerator().Emit(OpCodes.Ret);

        var onObserved = handlersBuilder.DefineMethod(
            "OnObserved",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(AvaloniaPropertyChangedEventArgs)]);
        onObserved.GetILGenerator().Emit(OpCodes.Ret);

        var onRoutedClass = handlersBuilder.DefineMethod(
            "OnRoutedClass",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(Button), typeof(RoutedEventArgs)]);
        onRoutedClass.GetILGenerator().Emit(OpCodes.Ret);

        var onRoutedInstance = handlersBuilder.DefineMethod(
            "OnRoutedInstance",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(object), typeof(RoutedEventArgs)]);
        onRoutedInstance.GetILGenerator().Emit(OpCodes.Ret);

        var onResourcesChanged = handlersBuilder.DefineMethod(
            "OnResourcesChanged",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(object), typeof(ResourcesChangedEventArgs)]);
        onResourcesChanged.GetILGenerator().Emit(OpCodes.Ret);

        var onRawInput = handlersBuilder.DefineMethod(
            "OnRawInput",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(RawInputEventArgs)]);
        onRawInput.GetILGenerator().Emit(OpCodes.Ret);

        _ = handlersBuilder.CreateType();

        using var stream = new MemoryStream();
        ab.Save(stream);
        stream.Position = 0;
        return alc.LoadFromStream(stream);
    }
}
