using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;
using Foundation;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private const float LocalTimeoutSeconds = 0.15f;
    private const float GlobalTimeoutSeconds = 0.65f;

    private static MessagingTimeoutObservation ProbeMessagingTimeout()
    {
        using var provider = StartProvider();
        var errorOutputTask = provider.StandardError.ReadToEndAsync();
        try
        {
            var providerProcessId = ReadProviderProcessId(provider);

            var firstPreexistingElement = CreateApplication(providerProcessId);
            var secondPreexistingElement = CreateApplication(providerProcessId);
            var systemWideElement = CreateSystemWideElement();
            if (firstPreexistingElement == 0 || secondPreexistingElement == 0 || systemWideElement == 0)
            {
                ReleaseIfNotNull(firstPreexistingElement);
                ReleaseIfNotNull(secondPreexistingElement);
                ReleaseIfNotNull(systemWideElement);
                throw new InvalidOperationException("Could not create the AX references required by the messaging-timeout probe.");
            }

            try
            {
                var globalConfigurationError = AXUIElementSetMessagingTimeout(systemWideElement, GlobalTimeoutSeconds);
                var postConfigurationElement = CreateApplication(providerProcessId);
                if (postConfigurationElement == 0)
                {
                    throw new InvalidOperationException("Could not create the post-configuration AX application reference.");
                }

                try
                {
                    var localConfigurationError = AXUIElementSetMessagingTimeout(firstPreexistingElement, LocalTimeoutSeconds);
                    var baseline = MeasureAttributeCall(secondPreexistingElement, AXAttributeConstants.Title);
                    var localReferenceCall = MeasureWhileProviderBlocked(provider, firstPreexistingElement, AXAttributeConstants.Title);
                    var equalPreexistingReferenceCall = MeasureWhileProviderBlocked(provider, secondPreexistingElement, AXAttributeConstants.Title);
                    var equalPostConfigurationReferenceCall = MeasureWhileProviderBlocked(provider, postConfigurationElement, AXAttributeConstants.Title);
                    using var attributes = NSArray.FromNSObjects(AXAttributeConstants.Role, AXAttributeConstants.Title, AXAttributeConstants.Enabled);
                    var blockedBatchCall = MeasureMultipleAttributesWhileProviderBlocked(provider, postConfigurationElement, attributes);
                    var blockedSequentialCalls = MeasureSequentialAttributesWhileProviderBlocked(provider, postConfigurationElement, [AXAttributeConstants.Role, AXAttributeConstants.Title, AXAttributeConstants.Enabled]);
                    var isProviderBoundaryObserved =
                        localReferenceCall.Error == AXError.CannotComplete &&
                        equalPreexistingReferenceCall.Error == AXError.CannotComplete &&
                        equalPostConfigurationReferenceCall.Error == AXError.CannotComplete;

                    Require(globalConfigurationError == AXError.Success, $"Configuring the system-wide AX timeout returned {globalConfigurationError}.");
                    Require(localConfigurationError == AXError.Success, $"Configuring the element-local AX timeout returned {localConfigurationError}.");
                    Require(CFEqual(firstPreexistingElement, secondPreexistingElement), "The preexisting timeout references were not CFEqual.");
                    Require(CFEqual(firstPreexistingElement, postConfigurationElement), "The post-configuration timeout reference was not CFEqual to the preexisting references.");

                    return new MessagingTimeoutObservation(
                        ProviderProcessId: providerProcessId,
                        LocalTimeoutSeconds: LocalTimeoutSeconds,
                        GlobalTimeoutSeconds: GlobalTimeoutSeconds,
                        FirstPreexistingPointer: FormatPointer(firstPreexistingElement),
                        SecondPreexistingPointer: FormatPointer(secondPreexistingElement),
                        PostConfigurationPointer: FormatPointer(postConfigurationElement),
                        AreReferencesCoreFoundationEqual: true,
                        GlobalConfigurationError: globalConfigurationError,
                        LocalConfigurationError: localConfigurationError,
                        BaselineCall: baseline,
                        LocalReferenceBlockedCall: localReferenceCall,
                        EqualPreexistingReferenceBlockedCall: equalPreexistingReferenceCall,
                        EqualPostConfigurationReferenceBlockedCall: equalPostConfigurationReferenceCall,
                        BlockedBatchCall: blockedBatchCall,
                        BlockedSequentialCalls: blockedSequentialCalls,
                        IsProviderBoundaryObserved: isProviderBoundaryObserved);
                }
                finally
                {
                    CFRelease(postConfigurationElement);
                }
            }
            finally
            {
                AXUIElementSetMessagingTimeout(systemWideElement, 0);
                CFRelease(firstPreexistingElement);
                CFRelease(secondPreexistingElement);
                CFRelease(systemWideElement);
            }
        }
        finally
        {
            StopProvider(provider);
            try
            {
                errorOutputTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Provider stderr is diagnostic only and must not hide the probe result.
            }
        }
    }

    private static VisualElementBatchedQueryObservation ProbeVisualElementBatchedQuery()
    {
        using var provider = StartProvider();
        var errorOutputTask = provider.StandardError.ReadToEndAsync();
        try
        {
            var providerProcessId = ReadProviderProcessId(provider);
            using var backend = new MacVisualElementBackend(new VisualContextPlatformOptions(
                TimeSpan.FromSeconds(GlobalTimeoutSeconds),
                TimeSpan.FromSeconds(GlobalTimeoutSeconds)));
            using var context = new VisualContext();
            using var retention = context.CreateRetention();
            using var nativeApplication = AXUIElement.ElementFromPid(providerProcessId) ??
                throw new InvalidOperationException("Could not create an AX application element for the batched-query probe.");
            using var nativeElement = GetAttributeAsElement(nativeApplication, AXAttributeConstants.FocusedUIElement, out var focusedElementError) ??
                throw new InvalidOperationException($"Could not copy the provider's focused AX element: {focusedElementError}.");
            var element = GetOrCreateAXElement(backend, retention, nativeElement);
            var requestedFields = VisualElementFields.Id |
                VisualElementFields.Type |
                VisualElementFields.States |
                VisualElementFields.Name |
                VisualElementFields.Text |
                VisualElementFields.Bounds;

            var enabledError = CopyBooleanAttribute(nativeElement, AXAttributeConstants.Enabled, out var isEnabled);
            Require(enabledError == AXError.Success && isEnabled == true, $"The raw scalar decoder returned {enabledError} and {isEnabled?.ToString() ?? "no value"} for AXEnabled.");

            var baselineResult = element.Query(new VisualElementQueryRequest(requestedFields, 256));
            var expectedBaselineFields = VisualElementFields.Id |
                VisualElementFields.Type |
                VisualElementFields.States |
                VisualElementFields.Text |
                VisualElementFields.Bounds;
            Require(baselineResult.Failure is null, $"The responsive VisualElement batch returned {baselineResult.Failure?.Kind}.");
            Require((baselineResult.AvailableFields & expectedBaselineFields) == expectedBaselineFields,
                $"The responsive batch did not decode all expected scalar fields: {baselineResult.AvailableFields}.");
            Require(baselineResult.Snapshot.Type == VisualElementType.TextEdit, $"The responsive batch decoded the provider text field as {baselineResult.Snapshot.Type}.");
            Require(baselineResult.Snapshot.TextPreview == "probe-value", $"The responsive batch decoded an unexpected value: {baselineResult.Snapshot.TextPreview ?? "no value"}.");
            Require(baselineResult.Snapshot.Bounds is { Width: > 0, Height: > 0 }, $"The responsive batch decoded invalid bounds: {baselineResult.Snapshot.Bounds}.");
            var baselineTextRead = element.ReadText(0, 256);
            Require(baselineTextRead == new VisualElementTextReadResult("probe-value", null, null),
                "The responsive VisualElement text reader returned an unexpected page.");

            SendProviderCommand(provider, "block");
            ReadProviderStatus(provider, "BLOCKED");
            VisualElementQueryResult result;
            VisualElementTextReadResult blockedTextRead;
            TimeSpan blockedTextElapsed;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                result = element.Query(new VisualElementQueryRequest(requestedFields, 256));
                stopwatch.Stop();
                var textStopwatch = Stopwatch.StartNew();
                blockedTextRead = element.ReadText(0, 256);
                blockedTextElapsed = textStopwatch.Elapsed;
            }
            finally
            {
                stopwatch.Stop();
                SendProviderCommand(provider, "resume");
                ReadProviderStatus(provider, "RESUMED");
            }

            var nativeError = (result.Failure?.Exception as AXException)?.Error;
            Require(nativeError == AXError.CannotComplete, $"The blocked VisualElement query returned {nativeError?.ToString() ?? "no AX error"}.");
            Require(result.AvailableFields == VisualElementFields.Id, $"The blocked query unexpectedly made remote fields available: {result.AvailableFields}.");
            Require(result.Snapshot.Id == element.Id, "The Context-owned ID was lost when the remote batch timed out.");
            Require(stopwatch.Elapsed < TimeSpan.FromSeconds(GlobalTimeoutSeconds * 2),
                $"The VisualElement query took {stopwatch.Elapsed.TotalMilliseconds:F2} ms and appears to have consumed more than one AX message timeout.");
            var blockedTextError = (blockedTextRead.Failure?.Exception as AXException)?.Error;
            Require(blockedTextError == AXError.CannotComplete, $"The blocked VisualElement text read returned {blockedTextError?.ToString() ?? "no AX error"}.");
            Require(blockedTextElapsed < TimeSpan.FromSeconds(GlobalTimeoutSeconds * 2),
                $"The VisualElement text read took {blockedTextElapsed.TotalMilliseconds:F2} ms and appears to have attempted a fallback after a provider timeout.");

            return new VisualElementBatchedQueryObservation(
                ProviderProcessId: providerProcessId,
                MessagingTimeoutSeconds: GlobalTimeoutSeconds,
                BaselineAvailableFields: baselineResult.AvailableFields,
                BaselineMissingFields: baselineResult.MissingFields,
                BaselineType: baselineResult.Snapshot.Type,
                BaselineName: baselineResult.Snapshot.Name,
                BaselineText: baselineResult.Snapshot.TextPreview,
                BaselineBounds: baselineResult.Snapshot.Bounds,
                IsEnabled: isEnabled == true,
                ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
                FailureKind: result.Failure?.Kind,
                NativeError: nativeError,
                AvailableFields: result.AvailableFields,
                MissingFields: result.MissingFields,
                ElementId: element.Id,
                BlockedTextElapsedMilliseconds: blockedTextElapsed.TotalMilliseconds,
                BlockedTextFailureKind: blockedTextRead.Failure?.Kind,
                BlockedTextNativeError: blockedTextError);
        }
        finally
        {
            StopProvider(provider);
            try
            {
                errorOutputTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Provider stderr is diagnostic only and must not hide the probe result.
            }
        }
    }

    private static Process StartProvider()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The native probe app-host path is unavailable.");
        var startInfo = new ProcessStartInfo(processPath, "--provider")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the native AppKit provider process.");
    }

    private static NativeCallObservation MeasureWhileProviderBlocked(Process provider, nint element, NSString attribute)
    {
        SendProviderCommand(provider, "block");
        ReadProviderStatus(provider, "BLOCKED");
        try
        {
            return MeasureAttributeCall(element, attribute);
        }
        finally
        {
            SendProviderCommand(provider, "resume");
            ReadProviderStatus(provider, "RESUMED");
        }
    }

    private static TimedMultipleAttributeCallObservation MeasureMultipleAttributesWhileProviderBlocked(Process provider, nint element, NSArray attributes)
    {
        SendProviderCommand(provider, "block");
        ReadProviderStatus(provider, "BLOCKED");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = CopyMultipleAttributes(element, attributes, AXCopyMultipleAttributeOptions.None);
            stopwatch.Stop();
            return new TimedMultipleAttributeCallObservation(stopwatch.Elapsed.TotalMilliseconds, result);
        }
        finally
        {
            SendProviderCommand(provider, "resume");
            ReadProviderStatus(provider, "RESUMED");
        }
    }

    private static SequentialAttributeCallsObservation MeasureSequentialAttributesWhileProviderBlocked(Process provider, nint element, IReadOnlyList<NSString> attributes)
    {
        SendProviderCommand(provider, "block");
        ReadProviderStatus(provider, "BLOCKED");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var calls = attributes.Select(attribute => MeasureAttributeCall(element, attribute)).ToArray();
            stopwatch.Stop();
            return new SequentialAttributeCallsObservation(stopwatch.Elapsed.TotalMilliseconds, calls);
        }
        finally
        {
            SendProviderCommand(provider, "resume");
            ReadProviderStatus(provider, "RESUMED");
        }
    }

    private static NativeCallObservation MeasureAttributeCall(nint element, NSString attribute)
    {
        var stopwatch = Stopwatch.StartNew();
        var error = AXUIElementCopyAttributeValue(element, attribute.Handle.Handle, out var value);
        stopwatch.Stop();
        ReleaseIfNotNull(value);
        return new NativeCallObservation(error, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static string ReadProviderStatus(Process provider, string expectedStatus)
    {
        var line = provider.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (line is null || !line.StartsWith(expectedStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected provider status {expectedStatus}, but received {line ?? "end of stream"}.");
        }

        return line;
    }

    private static int ReadProviderProcessId(Process provider)
    {
        var ready = ReadProviderStatus(provider, "READY");
        var fields = ready.Split('\t');
        if (fields.Length != 2 || !int.TryParse(fields[1], out var providerProcessId))
        {
            throw new InvalidOperationException($"The AppKit provider returned an invalid readiness message: {ready}");
        }

        return providerProcessId;
    }

    private static void SendProviderCommand(Process provider, string command)
    {
        provider.StandardInput.WriteLine(command);
        provider.StandardInput.Flush();
    }

    private static void StopProvider(Process provider)
    {
        if (provider.HasExited)
        {
            return;
        }

        try
        {
            SendProviderCommand(provider, "resume");
            SendProviderCommand(provider, "exit");
            if (!provider.WaitForExit(5_000))
            {
                provider.Kill(true);
                provider.WaitForExit(5_000);
            }
        }
        catch when (provider.HasExited)
        {
        }
    }

    private static void ReleaseIfNotNull(nint value)
    {
        if (value != 0)
        {
            CFRelease(value);
        }
    }

    [LibraryImport(ApplicationServices)]
    private static partial AXError AXUIElementSetMessagingTimeout(nint element, float timeoutInSeconds);

    [LibraryImport(ApplicationServices)]
    private static partial AXError AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CFEqual(nint first, nint second);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetAttributeAsElement")]
    private static extern AXUIElement? GetAttributeAsElement(AXUIElement element, NSString attributeName, out AXError error);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CopyBooleanAttribute")]
    private static extern AXError CopyBooleanAttribute(AXUIElement element, NSString attributeName, out bool? result);

    private sealed record MessagingTimeoutObservation(int ProviderProcessId, float LocalTimeoutSeconds, float GlobalTimeoutSeconds, string FirstPreexistingPointer, string SecondPreexistingPointer, string PostConfigurationPointer, bool AreReferencesCoreFoundationEqual, AXError GlobalConfigurationError, AXError LocalConfigurationError, NativeCallObservation BaselineCall, NativeCallObservation LocalReferenceBlockedCall, NativeCallObservation EqualPreexistingReferenceBlockedCall, NativeCallObservation EqualPostConfigurationReferenceBlockedCall, TimedMultipleAttributeCallObservation BlockedBatchCall, SequentialAttributeCallsObservation BlockedSequentialCalls, bool IsProviderBoundaryObserved);

    private sealed record NativeCallObservation(AXError Error, double ElapsedMilliseconds);

    private sealed record TimedMultipleAttributeCallObservation(double ElapsedMilliseconds, MultipleAttributeCallObservation Result);

    private sealed record SequentialAttributeCallsObservation(double ElapsedMilliseconds, IReadOnlyList<NativeCallObservation> Calls);

    private sealed record VisualElementBatchedQueryObservation(int ProviderProcessId, float MessagingTimeoutSeconds, VisualElementFields BaselineAvailableFields, VisualElementFields BaselineMissingFields, VisualElementType? BaselineType, string? BaselineName, string? BaselineText, PixelRect? BaselineBounds, bool IsEnabled, double ElapsedMilliseconds, VisualElementQueryFailureKind? FailureKind, AXError? NativeError, VisualElementFields AvailableFields, VisualElementFields MissingFields, string ElementId, double BlockedTextElapsedMilliseconds, VisualElementQueryFailureKind? BlockedTextFailureKind, AXError? BlockedTextNativeError);
}
