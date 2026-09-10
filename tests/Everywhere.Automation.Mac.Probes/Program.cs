using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using AppKit;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;
using Foundation;
using ObjCRuntime;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    public static int Main(string[] args)
    {
        var results = new List<ProbeResult>();
        try
        {
            NSApplication.Init();
            CGDisplayTopology.Initialize();
            if (args is ["--provider"])
            {
                return ProbeProviderHost.Run();
            }

            if (args is ["--panel-discovery"])
            {
                results.Add(RunProbe("panel-discovery", ProbePanelDiscovery));
            }
            else if (args is ["--query-semantics"])
            {
                results.Add(RunProbe("query-semantics", ProbeQuerySemantics));
            }
            else if (args is ["--capture"])
            {
                results.Add(RunProbe("capture-contract", ProbeCaptureContract));
            }
            else if (args is ["--content-boundaries"])
            {
                results.Add(RunProbe("content-boundaries", ProbeContentBoundaries));
            }
            else if (args is ["--provider-failure-survey"])
            {
                results.Add(RunProbe("provider-failure-survey", ProbeProviderFailureSurvey));
            }
            else if (args is ["--provider-failure-default-survey"])
            {
                results.Add(RunProbe("provider-failure-default-survey", ProbeProviderFailureDefaultSurvey));
            }
            else
            {
                results.Add(RunProbe("native-app-host", ProbeNativeAppHost));
                results.Add(RunProbe("ax-cf-identity", ProbeNativeIdentity));
                results.Add(RunProbe("ax-multiple-attributes", ProbeMultipleAttributes));
                results.Add(RunProbe("ax-visual-element-batched-query", ProbeVisualElementBatchedQuery));
                results.Add(RunProbe("ax-messaging-timeout", ProbeMessagingTimeout));
                results.Add(RunProbe("ax-concurrent-providers", ProbeConcurrentProviders));
                results.Add(RunProbe("context-identity-map", ProbeContextIdentityMap));
                results.Add(RunProbe("context-isolation", ProbeContextIsolation));
                results.Add(RunProbe("query-semantics", ProbeQuerySemantics));
                results.Add(RunProbe("window-states", ProbeWindowStates));
                results.Add(RunProbe("content-boundaries", ProbeContentBoundaries));
                results.Add(RunProbe("screen-topology-notification", ProbeScreenTopologyNotification));
            }
        }
        catch (Exception exception)
        {
            results.Add(ProbeResult.Failed("objc-runtime-initialization", exception));
        }

        var report = new ProbeReport(
            new EnvironmentReport(
                OperatingSystem: RuntimeInformation.OSDescription,
                OperatingSystemArchitecture: RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
                Framework: RuntimeInformation.FrameworkDescription,
                RuntimeVersion: Environment.Version.ToString(),
                ProcessPath: Environment.ProcessPath,
                BundlePath: NSBundle.MainBundle.BundlePath,
                BundleIdentifier: NSBundle.MainBundle.BundleIdentifier),
            results);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return results.All(static result => result.IsPassed) ? 0 : 1;
    }

    private static NativeAppHostObservation ProbeNativeAppHost()
    {
        var mainProgramHandle = NativeLibrary.GetMainProgramHandle();
        var hasXamarinInitializeExport = NativeLibrary.TryGetExport(mainProgramHandle, "xamarin_initialize", out var xamarinInitialize);
        Require(hasXamarinInitializeExport, "The current process does not export xamarin_initialize and is not a usable Microsoft.macOS app host.");
        return new NativeAppHostObservation(
            IsMachOAppHost: true,
            HasXamarinInitializeExport: hasXamarinInitializeExport,
            XamarinInitializeAddress: FormatPointer(xamarinInitialize));
    }

    private static NativeIdentityObservation ProbeNativeIdentity()
    {
        using var first = CreateCurrentApplicationElement();
        using var second = CreateCurrentApplicationElement();
        var firstPointer = first.Handle.Handle;
        var secondPointer = second.Handle.Handle;
        var isCoreFoundationEqual = first.Equals(second);
        var firstHash = first.GetHashCode();
        var secondHash = second.GetHashCode();

        Require(firstPointer != secondPointer, "Two AXUIElementCreateApplication calls returned the same pointer, so this run did not exercise CFEqual-based identity.");
        Require(isCoreFoundationEqual, "Two AX application references for the same PID were not CFEqual.");
        Require(firstHash == secondHash, "CFEqual AX application references returned different CFHash values.");

        return new NativeIdentityObservation(
            FirstPointer: FormatPointer(firstPointer),
            SecondPointer: FormatPointer(secondPointer),
            ArePointersDifferent: firstPointer != secondPointer,
            IsCoreFoundationEqual: isCoreFoundationEqual,
            FirstCoreFoundationHash: firstHash,
            SecondCoreFoundationHash: secondHash);
    }

    private static ContextIdentityMapObservation ProbeContextIdentityMap()
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

        Require(firstPointer != secondPointer, "The identity-map probe did not receive distinct native AX pointers.");
        Require(ReferenceEquals(firstElement, secondElement), "CFEqual AX references did not reuse the Context-owned canonical element.");
        Require(firstElement.Id == secondElement.Id, "The canonical element returned different backend IDs.");
        Require(firstElement.Id.StartsWith("ax:", StringComparison.Ordinal), "The AX element ID was not allocated in the backend AX domain.");

        firstRetention.Dispose();
        var retainedId = secondElement.Query(new VisualElementQueryRequest(VisualElementFields.Id, 0)).Snapshot.Id;
        Require(retainedId == firstElement.Id, "Releasing one retention invalidated an element still owned by another retention.");

        secondRetention.Dispose();
        var isReleasedAfterLastRetention = Throws<ObjectDisposedException>(() => firstElement.Query(new VisualElementQueryRequest(VisualElementFields.Id, 0)));
        Require(isReleasedAfterLastRetention, "The canonical element stayed usable after its last retention was released.");

        using var thirdRetention = context.CreateRetention();
        using var thirdNative = CreateCurrentApplicationElement();
        var thirdElement = GetOrCreateAXElement(backend, thirdRetention, thirdNative);
        Require(!ReferenceEquals(firstElement, thirdElement), "A released canonical element was reused by a later incarnation.");
        Require(firstElement.Id != thirdElement.Id, "The backend reused an AX element ID after the previous incarnation was released.");

        return new ContextIdentityMapObservation(
            FirstNativePointer: FormatPointer(firstPointer),
            SecondNativePointer: FormatPointer(secondPointer),
            AreNativePointersDifferent: firstPointer != secondPointer,
            IsCanonicalElementReused: ReferenceEquals(firstElement, secondElement),
            FirstId: firstElement.Id,
            SecondId: secondElement.Id,
            IdAfterOneRetentionReleased: retainedId,
            IsReleasedAfterLastRetention: isReleasedAfterLastRetention,
            LaterIncarnationId: thirdElement.Id,
            IsLaterIncarnationDistinct: !ReferenceEquals(firstElement, thirdElement));
    }

    private static MultipleAttributesObservation ProbeMultipleAttributes()
    {
        using var unsupportedAttribute = new NSString("AXEverywhereProbeUnsupportedAttribute");
        using var attributes = NSArray.FromNSObjects(AXAttributeConstants.Role, unsupportedAttribute, AXAttributeConstants.Title);
        var systemWideElement = CreateSystemWideElement();
        if (systemWideElement == 0)
        {
            throw new InvalidOperationException("AXUIElementCreateSystemWide returned a null reference.");
        }

        try
        {
            var systemWideResult = CopyMultipleAttributes(systemWideElement, attributes, AXCopyMultipleAttributeOptions.None);
            var target = NSWorkspace.SharedWorkspace.RunningApplications.FirstOrDefault(static application => application.BundleIdentifier == "com.apple.finder") ??
                throw new InvalidOperationException("Finder is not running, so no stable AX application target is available.");
            var applicationElement = CreateApplication(target.ProcessIdentifier);
            if (applicationElement == 0)
            {
                throw new InvalidOperationException("AXUIElementCreateApplication returned a null Finder reference.");
            }

            MultipleAttributeCallObservation positionalResult;
            MultipleAttributeCallObservation stopOnErrorResult;
            try
            {
                positionalResult = CopyMultipleAttributes(applicationElement, attributes, AXCopyMultipleAttributeOptions.None);
                stopOnErrorResult = CopyMultipleAttributes(applicationElement, attributes, AXCopyMultipleAttributeOptions.StopOnError);
            }
            finally
            {
                CFRelease(applicationElement);
            }

            var isProcessTrusted = AXIsProcessTrusted();
            var hasPositionalPerAttributeResults =
                positionalResult.Error == AXError.Success &&
                positionalResult.Values.Count == (int)attributes.Count &&
                positionalResult.Values[0].AXError is null &&
                positionalResult.Values[1].AXError is not null;
            return new MultipleAttributesObservation(
                Attributes: [AXAttributeConstants.Role.ToString(), unsupportedAttribute.ToString(), AXAttributeConstants.Title.ToString()],
                IsProcessTrusted: isProcessTrusted,
                TargetBundleIdentifier: target.BundleIdentifier,
                TargetProcessId: target.ProcessIdentifier,
                SystemWideResult: systemWideResult,
                ApplicationPositionalResult: positionalResult,
                ApplicationStopOnErrorResult: stopOnErrorResult,
                HasApplicationPositionalPerAttributeResults: hasPositionalPerAttributeResults);
        }
        finally
        {
            CFRelease(systemWideElement);
        }
    }

    private static ContextIsolationObservation ProbeContextIsolation()
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

        Require(firstNative.Equals(secondNative), "The context-isolation probe did not receive CFEqual AX references.");
        Require(!ReferenceEquals(firstElement, secondElement), "Different VisualContexts shared one managed AX element.");
        Require(firstElement.Id != secondElement.Id, "Different VisualContexts received the same backend AX ID.");

        return new ContextIsolationObservation(
            IsNativeIdentityEqual: firstNative.Equals(secondNative),
            IsManagedElementDistinct: !ReferenceEquals(firstElement, secondElement),
            FirstId: firstElement.Id,
            SecondId: secondElement.Id);
    }

    private static ProbeResult RunProbe<T>(string name, Func<T> probe) where T : notnull
    {
        try
        {
            return ProbeResult.Passed(name, probe());
        }
        catch (ProbeValidationException exception)
        {
            return ProbeResult.Failed(name, exception, exception.Observation);
        }
        catch (Exception exception)
        {
            return ProbeResult.Failed(name, exception);
        }
    }

    private static AXUIElement CreateCurrentApplicationElement() =>
        AXUIElement.ElementFromPid(Environment.ProcessId) ?? throw new InvalidOperationException("Could not create an AX application element for the probe process.");

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static void Require(bool isConditionSatisfied, string message)
    {
        if (!isConditionSatisfied)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string FormatPointer(nint pointer) => $"0x{pointer:x}";

    private static MultipleAttributeCallObservation CopyMultipleAttributes(nint element, NSArray attributes, AXCopyMultipleAttributeOptions options)
    {
        var error = AXUIElementCopyMultipleAttributeValues(element, attributes.Handle.Handle, options, out var valuesHandle);
        if (valuesHandle == 0)
        {
            return new MultipleAttributeCallObservation(error, 0, []);
        }

        using var values = Runtime.GetNSObject<NSArray>(valuesHandle, owns: true) ?? throw new InvalidOperationException("Could not wrap the AX multiple-attribute result array.");
        var observations = new List<CoreFoundationValueObservation>((int)values.Count);
        for (nuint index = 0; index < values.Count; index++)
        {
            var valueHandle = values.ValueAt(index).Handle;
            observations.Add(DescribeCoreFoundationValue((int)index, valueHandle));
        }

        return new MultipleAttributeCallObservation(error, (int)values.Count, observations);
    }

    private static CoreFoundationValueObservation DescribeCoreFoundationValue(int index, nint valueHandle)
    {
        if (valueHandle == 0)
        {
            return new CoreFoundationValueObservation(index, "null", null, null, null);
        }

        var typeId = CFGetTypeID(valueHandle);
        var typeDescriptionHandle = CFCopyTypeIDDescription(typeId);
        var typeName = $"CFTypeID:{typeId}";
        if (typeDescriptionHandle != 0)
        {
            try
            {
                typeName = ReadCoreFoundationString(typeDescriptionHandle) ?? typeName;
            }
            finally
            {
                CFRelease(typeDescriptionHandle);
            }
        }

        if (typeId == AXValueGetTypeID())
        {
            var valueType = AXValueGetType(valueHandle);
            AXError? error = null;
            if (valueType == AXValueType.AXError && TryReadAXError(valueHandle, out var errorValue))
            {
                error = errorValue;
            }

            return new CoreFoundationValueObservation(index, typeName, valueType, error, null);
        }

        var descriptionHandle = CFCopyDescription(valueHandle);
        if (descriptionHandle == 0)
        {
            return new CoreFoundationValueObservation(index, typeName, null, null, null);
        }

        try
        {
            return new CoreFoundationValueObservation(index, typeName, null, null, ReadCoreFoundationString(descriptionHandle));
        }
        finally
        {
            CFRelease(descriptionHandle);
        }
    }

    private static unsafe bool TryReadAXError(nint value, out AXError error)
    {
        error = default;
        fixed (AXError* errorPointer = &error)
        {
            return AXValueGetValue(value, AXValueType.AXError, (nint)errorPointer) != 0;
        }
    }

    private static unsafe string? ReadCoreFoundationString(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID())
        {
            return null;
        }

        var length = CFStringGetLength(value);
        if (length < 0 || length > int.MaxValue)
        {
            return null;
        }

        return string.Create((int)length, value, static (characters, stringValue) =>
        {
            fixed (char* charactersPointer = characters)
            {
                CFStringGetCharacters(stringValue, new NativeCFRange(0, characters.Length), charactersPointer);
            }
        });
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetOrCreateAXElement")]
    private static extern AXVisualElement GetOrCreateAXElement(MacVisualElementBackend backend, VisualElementRetention retention, AXUIElement nativeElement);

    [Flags]
    private enum AXCopyMultipleAttributeOptions : uint
    {
        None = 0,
        StopOnError = 1,
    }

    [LibraryImport(ApplicationServices, EntryPoint = "AXUIElementCreateSystemWide")]
    private static partial nint CreateSystemWideElement();

    [LibraryImport(ApplicationServices, EntryPoint = "AXUIElementCreateApplication")]
    private static partial nint CreateApplication(int processId);

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AXIsProcessTrusted();

    [LibraryImport(ApplicationServices)]
    private static partial AXError AXUIElementCopyMultipleAttributeValues(nint element, nint attributes, AXCopyMultipleAttributeOptions options, out nint values);

    [LibraryImport(ApplicationServices)]
    private static partial nuint AXValueGetTypeID();

    [LibraryImport(ApplicationServices)]
    private static partial AXValueType AXValueGetType(nint value);

    [LibraryImport(ApplicationServices)]
    private static partial byte AXValueGetValue(nint value, AXValueType valueType, nint valuePointer);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint value);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial void CFStringGetCharacters(nint value, NativeCFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFCopyTypeIDDescription(nuint typeId);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFCopyDescription(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeCFRange(nint Location, nint Length);

    private sealed record ProbeReport(EnvironmentReport Environment, IReadOnlyList<ProbeResult> Results);

    private sealed record EnvironmentReport(string OperatingSystem, string OperatingSystemArchitecture, string ProcessArchitecture, string Framework, string RuntimeVersion, string? ProcessPath, string BundlePath, string? BundleIdentifier);

    private sealed record ProbeResult(string Name, bool IsPassed, object? Observation, string? ErrorType, string? ErrorMessage)
    {
        internal static ProbeResult Passed(string name, object observation) => new(name, true, observation, null, null);

        internal static ProbeResult Failed(string name, Exception exception, object? observation = null) => new(name, false, observation, exception.GetType().FullName, exception.ToString());
    }

    private sealed class ProbeValidationException(string message, object observation) : Exception(message)
    {
        internal object Observation { get; } = observation;
    }

    private sealed record NativeAppHostObservation(bool IsMachOAppHost, bool HasXamarinInitializeExport, string XamarinInitializeAddress);

    private sealed record NativeIdentityObservation(string FirstPointer, string SecondPointer, bool ArePointersDifferent, bool IsCoreFoundationEqual, int FirstCoreFoundationHash, int SecondCoreFoundationHash);

    private sealed record MultipleAttributesObservation(IReadOnlyList<string> Attributes, bool IsProcessTrusted, string? TargetBundleIdentifier, int TargetProcessId, MultipleAttributeCallObservation SystemWideResult, MultipleAttributeCallObservation ApplicationPositionalResult, MultipleAttributeCallObservation ApplicationStopOnErrorResult, bool HasApplicationPositionalPerAttributeResults);

    private sealed record MultipleAttributeCallObservation(AXError Error, int ValueCount, IReadOnlyList<CoreFoundationValueObservation> Values);

    private sealed record CoreFoundationValueObservation(int Index, string CoreFoundationType, AXValueType? AXValueType, AXError? AXError, string? Description);

    private sealed record ContextIdentityMapObservation(string FirstNativePointer, string SecondNativePointer, bool AreNativePointersDifferent, bool IsCanonicalElementReused, string FirstId, string SecondId, string? IdAfterOneRetentionReleased, bool IsReleasedAfterLastRetention, string LaterIncarnationId, bool IsLaterIncarnationDistinct);

    private sealed record ContextIsolationObservation(bool IsNativeIdentityEqual, bool IsManagedElementDistinct, string FirstId, string SecondId);
}
