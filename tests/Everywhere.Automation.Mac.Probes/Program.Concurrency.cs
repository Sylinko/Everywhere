using System.Diagnostics;
using Everywhere.Mac.Interop;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private static ConcurrentProvidersObservation ProbeConcurrentProviders()
    {
        using var blockedProvider = StartProvider();
        using var responsiveProvider = StartProvider();
        var blockedErrorOutputTask = blockedProvider.StandardError.ReadToEndAsync();
        var responsiveErrorOutputTask = responsiveProvider.StandardError.ReadToEndAsync();
        try
        {
            var blockedProviderProcessId = ReadProviderProcessId(blockedProvider);
            var responsiveProviderProcessId = ReadProviderProcessId(responsiveProvider);
            var blockedElement = CreateApplication(blockedProviderProcessId);
            var responsiveElement = CreateApplication(responsiveProviderProcessId);
            var systemWideElement = CreateSystemWideElement();
            if (blockedElement == 0 || responsiveElement == 0 || systemWideElement == 0)
            {
                ReleaseIfNotNull(blockedElement);
                ReleaseIfNotNull(responsiveElement);
                ReleaseIfNotNull(systemWideElement);
                throw new InvalidOperationException("Could not create the AX references required by the concurrency probe.");
            }

            try
            {
                var globalConfigurationError = AXUIElementSetMessagingTimeout(systemWideElement, GlobalTimeoutSeconds);
                Require(globalConfigurationError == AXError.Success, $"Configuring the concurrency-probe timeout returned {globalConfigurationError}.");
                var baselineResponsiveCall = MeasureAttributeCall(responsiveElement, AXAttributeConstants.Title);
                SendProviderCommand(blockedProvider, "block");
                ReadProviderStatus(blockedProvider, "BLOCKED");
                NativeCallObservation blockedCall;
                NativeCallObservation responsiveCall;
                bool wasBlockedCallPendingAtResponsiveStart;
                bool didResponsiveCallCompleteWhileBlocked;
                try
                {
                    using var blockedCallStarted = new ManualResetEventSlim();
                    var blockedCallTask = Task.Run(() =>
                    {
                        blockedCallStarted.Set();
                        return MeasureAttributeCall(blockedElement, AXAttributeConstants.Title);
                    });
                    Require(blockedCallStarted.Wait(TimeSpan.FromSeconds(2)), "The blocked AX call did not start.");
                    Thread.Sleep(75);
                    wasBlockedCallPendingAtResponsiveStart = !blockedCallTask.IsCompleted;
                    responsiveCall = MeasureAttributeCall(responsiveElement, AXAttributeConstants.Title);
                    didResponsiveCallCompleteWhileBlocked = !blockedCallTask.IsCompleted;
                    blockedCall = blockedCallTask.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }
                finally
                {
                    SendProviderCommand(blockedProvider, "resume");
                    ReadProviderStatus(blockedProvider, "RESUMED");
                }

                return new ConcurrentProvidersObservation(
                    BlockedProviderProcessId: blockedProviderProcessId,
                    ResponsiveProviderProcessId: responsiveProviderProcessId,
                    MessagingTimeoutSeconds: GlobalTimeoutSeconds,
                    BaselineResponsiveCall: baselineResponsiveCall,
                    WasBlockedCallPendingAtResponsiveStart: wasBlockedCallPendingAtResponsiveStart,
                    DidResponsiveCallCompleteWhileBlocked: didResponsiveCallCompleteWhileBlocked,
                    BlockedProviderCall: blockedCall,
                    ResponsiveProviderCall: responsiveCall);
            }
            finally
            {
                AXUIElementSetMessagingTimeout(systemWideElement, 0);
                CFRelease(blockedElement);
                CFRelease(responsiveElement);
                CFRelease(systemWideElement);
            }
        }
        finally
        {
            StopProvider(blockedProvider);
            StopProvider(responsiveProvider);
            WaitForDiagnosticOutput(blockedErrorOutputTask);
            WaitForDiagnosticOutput(responsiveErrorOutputTask);
        }
    }

    private static void WaitForDiagnosticOutput(Task<string> outputTask)
    {
        try
        {
            outputTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Provider stderr is diagnostic only and must not hide the probe result.
        }
    }

    private sealed record ConcurrentProvidersObservation(int BlockedProviderProcessId, int ResponsiveProviderProcessId, float MessagingTimeoutSeconds, NativeCallObservation BaselineResponsiveCall, bool WasBlockedCallPendingAtResponsiveStart, bool DidResponsiveCallCompleteWhileBlocked, NativeCallObservation BlockedProviderCall, NativeCallObservation ResponsiveProviderCall);
}
