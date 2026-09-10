using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Resolves operation-local Quartz window identifiers to retained AXWindow references through bounded per-provider paging.
/// </summary>
public sealed class AXWindowResolver : IDisposable
{
    private const int MaximumProviderCount = 256;
    private const int MaximumWindowsPerProvider = 4_096;
    private const int WindowPageSize = 64;

    private readonly Dictionary<int, HashSet<uint>> _targetWindowIdsByProcess;
    private readonly Dictionary<int, ProviderWindowMap> _providerWindows = [];
    private bool _isDisposed;

    public AXWindowResolver(IEnumerable<CGWindowZOrder.Entry> windows)
        : this(windows.Select(static window => new AXWindowReference(window.WindowId, window.OwnerProcessId)))
    {
    }

    public AXWindowResolver(IEnumerable<AXWindowReference> windows)
    {
        _targetWindowIdsByProcess = windows
            .GroupBy(static window => window.OwnerProcessId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static window => window.WindowId).ToHashSet());
        if (_targetWindowIdsByProcess.Count > MaximumProviderCount)
        {
            throw new InvalidOperationException($"The macOS window observation exceeded the {MaximumProviderCount}-provider safety limit.");
        }
    }

    public AXUIElement? Resolve(CGWindowZOrder.Entry window) => Resolve(new AXWindowReference(window.WindowId, window.OwnerProcessId));

    public AXUIElement? Resolve(AXWindowReference window)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_targetWindowIdsByProcess.TryGetValue(window.OwnerProcessId, out var value))
        {
            throw new InvalidOperationException("The requested window was not part of this resolver's operation-local target set.");
        }

        if (!_providerWindows.TryGetValue(window.OwnerProcessId, out var providerWindows))
        {
            providerWindows = LoadProviderWindows(window.OwnerProcessId, value);
            _providerWindows.Add(window.OwnerProcessId, providerWindows);
        }

        return providerWindows.Find(window.WindowId)?.Retain();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var providerWindows in _providerWindows.Values)
        {
            providerWindows.Dispose();
        }

        _providerWindows.Clear();
    }

    private static ProviderWindowMap LoadProviderWindows(int processId, IReadOnlySet<uint> targetWindowIds)
    {
        var result = new ProviderWindowMap();
        using var application = AXUIElement.ElementFromPid(processId);
        if (application is null)
        {
            return result;
        }

        try
        {
            var countError = application.GetAttributeValueCount(AXAttributeConstants.Windows, out var count);
            countError.ThrowIfProviderFailure("count AX application windows");
            if (countError != AXError.Success || count <= 0)
            {
                return result;
            }

            var scanCount = Math.Min(count, MaximumWindowsPerProvider);
            var remainingWindowIds = targetWindowIds.ToHashSet();
            for (nint pageStart = 0; pageStart < scanCount && remainingWindowIds.Count > 0; pageStart += WindowPageSize)
            {
                var pageLength = Math.Min(WindowPageSize, scanCount - pageStart);
                var error = application.CopyAttributeValues(AXAttributeConstants.Windows, pageStart, pageLength, out var values);
                using (values)
                {
                    error.ThrowIfProviderFailure("copy an AX application window page");
                    if (error != AXError.Success || values is null)
                    {
                        return result;
                    }

                    for (nuint pageIndex = 0; pageIndex < values.Count; pageIndex++)
                    {
                        var element = AXUIElement.FromArray(values, pageIndex);
                        if (element is null)
                        {
                            continue;
                        }

                        var windowIdError = element.GetNativeWindowHandle(out var windowId);
                        if (windowIdError is AXError.CannotComplete or AXError.APIDisabled)
                        {
                            element.Dispose();
                            throw new AXException(windowIdError, $"Failed to map an AX application window to Quartz. AX returned {windowIdError}.");
                        }

                        if (windowIdError != AXError.Success || windowId == 0 || !remainingWindowIds.Contains(windowId))
                        {
                            element.Dispose();
                            continue;
                        }

                        var roleError = element.CopyStringAttribute(AXAttributeConstants.Role, out var role);
                        roleError.ThrowIfProviderFailure("read the AX role while mapping a Quartz window");
                        if (roleError != AXError.Success || role != nameof(AXRoleAttribute.AXWindow))
                        {
                            element.Dispose();
                            continue;
                        }

                        result.Add(windowId, element);
                        remainingWindowIds.Remove(windowId);
                    }
                }
            }

            if (count > MaximumWindowsPerProvider && remainingWindowIds.Count > 0)
            {
                throw new InvalidOperationException($"The AX application window list exceeded the {MaximumWindowsPerProvider}-element safety limit before every visible Quartz window could be resolved.");
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public readonly record struct AXWindowReference(uint WindowId, int OwnerProcessId);

    private sealed class ProviderWindowMap : IDisposable
    {
        private readonly Dictionary<uint, AXUIElement> _windows = [];

        internal void Add(uint windowId, AXUIElement element)
        {
            if (!_windows.TryAdd(windowId, element))
            {
                element.Dispose();
            }
        }

        internal AXUIElement? Find(uint windowId) => _windows.GetValueOrDefault(windowId);

        public void Dispose()
        {
            foreach (var window in _windows.Values)
            {
                window.Dispose();
            }

            _windows.Clear();
        }
    }
}
