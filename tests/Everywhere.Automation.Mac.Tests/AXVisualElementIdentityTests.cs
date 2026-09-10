using System.Runtime.CompilerServices;
using AppKit;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Automation.Mac.Tests;

[NonParallelizable]
[Explicit("Requires the native Microsoft.macOS app host. Run Everywhere.Automation.Mac.Probes instead of VSTest.")]
public sealed class AXVisualElementIdentityTests
{
    [OneTimeSetUp]
    public void InitializeNativeRuntime() => NSApplication.Init();

    [Test]
    public void EqualNativeElements_WhenPointersDiffer_ReuseCanonicalElementAndId()
    {
        using var backend = new MacVisualElementBackend();
        using var context = new VisualContext();
        var firstRetention = context.CreateRetention();
        var secondRetention = context.CreateRetention();

        AXVisualElement firstElement;
        nint firstPointer;
        using (var firstNative = CreateCurrentApplicationElement())
        {
            firstPointer = firstNative.Handle.Handle;
            firstElement = GetOrCreateAXElement(backend, firstRetention, firstNative);
        }

        AXVisualElement secondElement;
        nint secondPointer;
        using (var secondNative = CreateCurrentApplicationElement())
        {
            secondPointer = secondNative.Handle.Handle;
            secondElement = GetOrCreateAXElement(backend, secondRetention, secondNative);
        }

        Assert.Multiple(() =>
        {
            Assert.That(secondPointer, Is.Not.EqualTo(firstPointer));
            Assert.That(secondElement, Is.SameAs(firstElement));
            Assert.That(secondElement.Id, Is.EqualTo(firstElement.Id));
            Assert.That(firstElement.Id, Does.StartWith("ax:"));
        });

        firstRetention.Dispose();
        Assert.That(secondElement.Query(new VisualElementQueryRequest(VisualElementFields.Id, 0)).Snapshot.Id, Is.EqualTo(firstElement.Id));

        secondRetention.Dispose();
        Assert.Throws<ObjectDisposedException>(() => firstElement.Query(new VisualElementQueryRequest(VisualElementFields.Id, 0)));

        using var thirdRetention = context.CreateRetention();
        using var thirdNative = CreateCurrentApplicationElement();
        var thirdElement = GetOrCreateAXElement(backend, thirdRetention, thirdNative);
        Assert.Multiple(() =>
        {
            Assert.That(thirdElement, Is.Not.SameAs(firstElement));
            Assert.That(thirdElement.Id, Is.Not.EqualTo(firstElement.Id));
        });
    }

    [Test]
    public void EqualNativeElement_WhenContextsDiffer_ReceivesDifferentBackendIds()
    {
        using var backend = new MacVisualElementBackend();
        using var firstContext = new VisualContext();
        using var secondContext = new VisualContext();
        using var firstRetention = firstContext.CreateRetention();
        using var secondRetention = secondContext.CreateRetention();
        using var firstNative = CreateCurrentApplicationElement();
        using var secondNative = CreateCurrentApplicationElement();

        var firstElement = GetOrCreateAXElement(backend, firstRetention, firstNative);
        var secondElement = GetOrCreateAXElement(backend, secondRetention, secondNative);

        Assert.Multiple(() =>
        {
            Assert.That(secondNative.Equals(firstNative), Is.True);
            Assert.That(secondElement, Is.Not.SameAs(firstElement));
            Assert.That(secondElement.Id, Is.Not.EqualTo(firstElement.Id));
        });
    }

    private static AXUIElement CreateCurrentApplicationElement() =>
        AXUIElement.ElementFromPid(Environment.ProcessId) ?? throw new InvalidOperationException("Could not create an AX application element for the test process.");

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetOrCreateAXElement")]
    private static extern AXVisualElement GetOrCreateAXElement(
        MacVisualElementBackend backend,
        VisualElementRetention retention,
        AXUIElement nativeElement);
}
