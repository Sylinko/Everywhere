using System.Diagnostics;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Automation;

partial class LinuxVisualElementBackend
{
    /// <summary>
    /// Consumes the owned window proxy and retains only the confirmed native target.
    /// </summary>
    private VisualElementQueryResult QueryAccessible(
        VisualElementRetention retention,
        CompositorWindowInfo window,
        nint root,
        VisualElementLocator locator,
        PixelPoint point,
        VisualElementQueryRequest request)
    {
        return Accessibility.Call(() =>
        {
            using var search = new AccessibleSearch(root);
            // Chromium enables its renderer tree when an assistive client requests attributes.
            var attributes = AtspiNative.Checked(AtspiNative.atspi_accessible_get_attributes(root, out var error), error);
            if (attributes != 0)
            {
                AtspiNative.g_hash_table_unref(attributes);
            }

            var selected = locator.Kind switch
            {
                VisualElementLocatorKind.Point or VisualElementLocatorKind.Pointer =>
                    FindElementAtPoint(root, point - window.ClientRect.Position, window, search),
                VisualElementLocatorKind.Focused => FindFocusedElement(root, search),
                _ => root,
            };
            var result = AtspiVisualElement.GetOrCreate(retention, this, window, selected).Query(request);
            if (locator.Kind is VisualElementLocatorKind.Point or VisualElementLocatorKind.Pointer
                && request.RequestedFields.HasFlag(VisualElementFields.Bounds)
                && result.Snapshot.Bounds is null)
            {
                // Point selection needs a real rectangle. Keep semantic queries
                // unchanged, but avoid replacing a usable ancestor with an unplaceable leaf.
                for (var index = search.HitPath.Count - 2; index >= 0 && search.CanContinue; index--)
                {
                    var ancestor = AtspiVisualElement.GetOrCreate(retention, this, window, search.HitPath[index]).Query(request);
                    if (ancestor.Snapshot.Bounds is not { } bounds || !bounds.Contains(point))
                    {
                        continue;
                    }

                    result = ancestor;
                    search.Report(
                        VisualElementQueryFailureKind.Unsupported,
                        "The deepest hit has no usable bounds; its nearest confirmed ancestor was returned.");
                    break;
                }
            }

            return result.Failure is null && search.Failure is not null ? result with
            {
                Failure = search.Failure,
            }

                : result;
        });
    }

    private nint FindElementAtPoint(nint root, PixelVector local, CompositorWindowInfo window, AccessibleSearch search)
    {
        var selected = root;
        var point = new PixelPoint(local.X, local.Y);
        while (search.CanContinue)
        {
            if (AtspiNative.IsDetachedEmbeddedRoot(selected))
            {
                break;
            }

            var component = AtspiNative.atspi_accessible_get_component_iface(selected);
            var child = default(nint);
            if (component != 0)
            {
                try
                {
                    child = AtspiNative.Checked(
                        AtspiNative.atspi_component_get_accessible_at_point(component, point.X, point.Y, AtspiCoordinates.Window, out var error),
                        error);
                }
                catch (TimeoutException)
                {
                    search.Report(
                        VisualElementQueryFailureKind.Timeout,
                        "The provider's point query timed out; the last confirmed ancestor is retained.");
                    break;
                }
                catch (VisualElementProviderException)
                {
                }
                finally
                {
                    AtspiNative.g_object_unref(component);
                }
            }

            if (child == selected)
            {
                AtspiNative.g_object_unref(child);
                child = 0;
            }

            if (child != 0)
            {
                if (!search.Adopt(child))
                {
                    search.Report(VisualElementQueryFailureKind.ProviderFailure, "The provider returned a cycle while resolving the point.");
                    break;
                }
            }
            else
            {
                // A logical wrapper may have children without implementing Component.
                // A provider returning itself also needs this bounded fallback.
                child = FindChildAtPoint(selected, point, window, search);
            }

            if (child == 0)
            {
                break;
            }

            selected = child;
            search.HitPath.Add(child);
        }

        return selected;
    }

    private nint FindChildAtPoint(nint parent, PixelPoint point, CompositorWindowInfo window, AccessibleSearch search)
    {
        if (IsBoundsQueryBackedOff(window) || AtspiNative.HasState(parent, AtspiState.ManagesDescendants))
        {
            return 0;
        }

        var count = AtspiNative.Count(parent);
        // Search from the actual end, not the end of a truncated prefix. Later
        // children commonly contain overlapping foreground controls.
        for (var index = count - 1; index >= 0 && search.CanContinue; index--)
        {
            var child = AtspiNative.Child(parent, index);
            if (!search.Adopt(child))
            {
                continue;
            }

            try
            {
                if (AtspiNative.IsDetachedEmbeddedRoot(child))
                {
                    continue;
                }

                var bounds = AtspiNative.ReadExtents(child, AtspiCoordinates.Window);
                if (bounds is { } rectangle)
                {
                    if (rectangle.Contains(point))
                    {
                        return child;
                    }

                    continue;
                }

                // Missing wrapper geometry does not prove that its descendants
                // are unavailable. Only return a descendant with confirmed bounds.
                var descendant = FindChildAtPoint(child, point, window, search);
                if (descendant != 0)
                {
                    return descendant;
                }
            }
            catch (TimeoutException)
            {
                RecordBoundsTimeout(window);
                search.Report(
                    VisualElementQueryFailureKind.Timeout,
                    "The provider's bounds query timed out; the last confirmed ancestor is retained.");
                return 0;
            }
            catch (VisualElementProviderException)
            {
            }
        }

        return 0;
    }

    private nint FindFocusedElement(nint root, AccessibleSearch search)
    {
        var focused = Accessibility.GetFocusedDescendant(root);
        if (focused != 0 && search.Adopt(focused))
        {
            return focused;
        }

        var pending = new Queue<nint>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var item) && search.CanContinue)
        {
            if (AtspiNative.HasState(item, AtspiState.Focused))
            {
                return item;
            }

            if (AtspiNative.HasState(item, AtspiState.ManagesDescendants))
            {
                continue;
            }

            var count = AtspiNative.Count(item);
            for (var index = 0; index < count && search.CanContinue; index++)
            {
                var child = AtspiNative.Child(item, index);
                if (search.Adopt(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return root;
    }

    /// <summary>
    /// Owns visited proxies until traversal ends, preventing pointer reuse from
    /// looking like a cycle. The same node/time bounds cover direct hits and fallback.
    /// </summary>
    private sealed class AccessibleSearch(nint root) : IDisposable
    {
        public VisualElementQueryFailure? Failure { get; private set; }
        public List<nint> HitPath { get; } = [root];

        public bool CanContinue
        {
            get
            {
                if (_deadline.ElapsedMilliseconds >= AtspiQueryLimits.MaximumElapsedMilliseconds)
                {
                    Report(VisualElementQueryFailureKind.LimitReached, "The accessibility search reached its elapsed-time limit.");
                }
                else if (_references.Count >= AtspiQueryLimits.MaximumNodes)
                {
                    Report(VisualElementQueryFailureKind.LimitReached, "The accessibility search reached its node limit.");
                }
                else
                {
                    return true;
                }

                return false;
            }
        }

        private readonly Stopwatch _deadline = Stopwatch.StartNew();
        private readonly HashSet<nint> _visited = [root];
        private readonly List<nint> _references = [root];
        // Takes ownership even when this proxy was already visited.
        public bool Adopt(nint pointer)
        {
            if (pointer == 0)
            {
                return false;
            }

            if (!_visited.Add(pointer))
            {
                AtspiNative.g_object_unref(pointer);
                return false;
            }

            _references.Add(pointer);
            return true;
        }

        public void Report(VisualElementQueryFailureKind kind, string message)
        {
            Failure ??= new VisualElementQueryFailure(kind, null, agentMessage: message);
        }

        public void Dispose()
        {
            foreach (var pointer in _references)
            {
                AtspiNative.g_object_unref(pointer);
            }
        }
    }

    /// <summary>
    /// Returns an owned AT-SPI window only when PID and window identity are unambiguous.
    /// A requested refresh clears only this client's matching application cache.
    /// </summary>
    public nint FindAccessibleWindow(CompositorWindowInfo window, bool shouldRefresh = false)
    {
        var desktop = default(nint);
        try
        {
            desktop = Accessibility.Call(() => AtspiNative.atspi_get_desktop(0));
        }
        catch (NotSupportedException)
        {
            return 0;
        }

        if (desktop == 0)
        {
            return 0;
        }

        try
        {
            var applications = Math.Min(AtspiNative.Count(desktop), AtspiQueryLimits.MaximumNodes);
            for (var index = 0; index < applications; index++)
            {
                var application = AtspiNative.Child(desktop, index);
                if (application == 0)
                {
                    continue;
                }

                try
                {
                    if (AtspiNative.Pid(application) != window.ProcessId)
                    {
                        continue;
                    }

                    if (shouldRefresh)
                    {
                        AtspiNative.atspi_accessible_clear_cache(application);
                    }

                    var count = AtspiNative.Count(application);
                    if (count > MaximumApplicationWindows)
                    {
                        return 0;
                    }

                    var candidate = default(nint);
                    var matches = 0;
                    try
                    {
                        for (var childIndex = 0; childIndex < count; childIndex++)
                        {
                            var child = AtspiNative.Child(application, childIndex);
                            if (child == 0)
                            {
                                continue;
                            }

                            var name = AtspiNative.Name(child);
                            if (count == 1 || name == window.Title && !string.IsNullOrEmpty(window.Title))
                            {
                                matches++;
                                if (candidate != 0)
                                {
                                    AtspiNative.g_object_unref(candidate);
                                }

                                candidate = child;
                            }
                            else
                            {
                                AtspiNative.g_object_unref(child);
                            }
                        }

                        if (matches != 1)
                        {
                            return 0;
                        }

                        var result = candidate;
                        candidate = 0;
                        return result;
                    }
                    finally
                    {
                        if (candidate != 0)
                        {
                            AtspiNative.g_object_unref(candidate);
                        }
                    }
                }
                finally
                {
                    AtspiNative.g_object_unref(application);
                }
            }

            return 0;
        }
        finally
        {
            AtspiNative.g_object_unref(desktop);
        }
    }
}