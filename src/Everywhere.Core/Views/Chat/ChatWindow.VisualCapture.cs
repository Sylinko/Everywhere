using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Microsoft.Extensions.Logging;

namespace Everywhere.Views;

/// <summary>
/// Coordinates the ChatWindow-specific capture lifecycle while the shared effect host owns rendering resources.
/// </summary>
partial class ChatWindow : IDisposable
{
    private bool IsEffectiveVisible => _windowHelper.GetEffectiveVisible(this);

    private readonly IScreenSelectionService _screenSelectionService;
    private readonly ChatVisualService _visualService;
    private readonly VisualElementEffect _visualElementEffect;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<CaptureOperation> _operations = [];

    private long _nextOperationId;
    private bool _isPreparing;
    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        ViewModel.VisualElementCaptureRequested = null;

        _lifetimeCancellation.Cancel();
        foreach (var operation in _operations.ToArray()) operation.Cancel();
        _operations.Clear();
        _lifetimeCancellation.Dispose();
    }

    private bool TryRestoreCaptureWindow(long interactionVersion)
    {
        if (interactionVersion != _cloakInteractionVersion) return false;

        ApplyCloakedCore(false);
        return true;
    }

    private async Task CaptureAsync(VisualElementCaptureRequest request, CancellationToken cancellationToken)
    {
        if (_isDisposed || _isPreparing) return;

        var operationId = ++_nextOperationId;
        _logger.LogDebug("Visual element capture {OperationId} started preparation", operationId);
        _isPreparing = true;
        var restoreWindow = request.Locator is not null;
        var initialCloakInteractionVersion = _cloakInteractionVersion;
        CapturedVisualElement? unacceptedCapture = null;
        RemoteVisualAnchor? unacceptedAnchor = null;
        VisualElementAttachment? unacceptedAttachment = null;
        VisualElementAttachment? pendingPresentationAttachment = null;
        var presentationTransferred = false;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            var operationCancellation = linkedCancellation.Token;

            RemoteVisualAnchor? anchor;
            if (request.Locator is { } locator)
            {
                ViewModel.PrepareChatWindowActivation();
                if (!ViewModel.CanAddAttachment) return;
                anchor = await _visualService.AcquireAnchorAsync(
                    locator,
                    request.Resolution,
                    cancellationToken: operationCancellation);
            }
            else
            {
                if (!ViewModel.CanAddAttachment) return;

                restoreWindow = IsEffectiveVisible;
                if (restoreWindow) ApplyCloakedCore(true);
                anchor = await _screenSelectionService.PickVisualElementAsync(
                    _visualService.AcquisitionContext,
                    null,
                    operationCancellation);
            }

            operationCancellation.ThrowIfCancellationRequested();
            if (anchor is null) return;

            unacceptedAnchor = anchor;
            restoreWindow = true;
            if (!ViewModel.CanAddVisualElement(anchor)) return;

            var attachment = VisualElementAttachment.FromRemoteAnchor(anchor);
            unacceptedAttachment = attachment;
            unacceptedAnchor = null;

            CapturedVisualElement? capture = null;
            if (ViewModel.Settings.ChatWindow.EnableVisualElementPickAnimation)
            {
                _visualElementEffect.ArrangeEffectWindows();
                capture = await CaptureElementAsync(attachment, operationCancellation);
                unacceptedCapture = capture;
            }

            operationCancellation.ThrowIfCancellationRequested();
            if (capture is not null)
            {
                attachment.PreviewImage = capture.Bitmap;
                if (ChatInputArea.RegisterPendingAttachment(attachment))
                    pendingPresentationAttachment = attachment;
            }

            if (!ViewModel.TryAddVisualElementAttachment(attachment)) return;

            // From this point the accepted attachment owns its preview image.
            unacceptedCapture = null;
            unacceptedAttachment = null;
            _logger.LogDebug("Visual element capture {OperationId} admitted its attachment", operationId);

            var windowRestored = TryRestoreCaptureWindow(initialCloakInteractionVersion);
            restoreWindow = false;

            if (capture is null || pendingPresentationAttachment is null) return;
            if (!windowRestored && !IsEffectiveVisible) return;

            var operation = new CaptureOperation(this, attachment, operationId);
            _operations.Add(operation);
            presentationTransferred = true;
            try
            {
                if (!_visualElementEffect.PlayPickEffect(capture.SourceBounds, capture.Bitmap, attachment, operation))
                    operation.Cancel();
            }
            catch
            {
                operation.Cancel();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture a visual element for the chat input");
        }
        finally
        {
            if (!presentationTransferred && pendingPresentationAttachment is not null)
                ChatInputArea.CancelAttachmentAcceptance(pendingPresentationAttachment);

            if (unacceptedCapture is not null)
            {
                unacceptedAttachment?.PreviewImage = null;
                unacceptedCapture.Bitmap.Dispose();
            }

            unacceptedAttachment?.Dispose();
            unacceptedAnchor?.Dispose();

            _isPreparing = false;
            if (restoreWindow && !_isDisposed)
                TryRestoreCaptureWindow(initialCloakInteractionVersion);
        }
    }

    private async static Task<CapturedVisualElement?> CaptureElementAsync(
        VisualElementAttachment attachment,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var capture = await attachment.CaptureAsync(timeoutCancellation.Token);
            if (capture.Bounds.Width <= 0 || capture.Bounds.Height <= 0) return null;
            var bitmap = capture.ToAvaloniaBitmap();
            return bitmap is null ? null : new CapturedVisualElement(capture.Bounds, bitmap);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void OperationEnded(CaptureOperation operation) => _operations.Remove(operation);

    private sealed record CapturedVisualElement(PixelRect SourceBounds, Bitmap Bitmap);

    private sealed class CaptureOperation(
        ChatWindow owner,
        VisualElementAttachment attachment,
        long operationId
    ) : IParticleTargetTracker
    {
        public bool IsCompleted { get; private set; }

        private readonly long _initialInteractionVersion = owner.ChatInputArea.AttachmentInteractionVersion;
        private readonly CancellationTokenSource _handoffCancellation = new();

        private long _lastOcclusionCheckMilliseconds;
        private bool _hasLocatedTarget;
        private bool _acceptanceStarted;
        private bool _handoffStarted;
        private bool _isCancelled;

        public bool IsCancelled
        {
            get
            {
                if (IsCompleted) return false;
                if (_isCancelled) return true;
                if (!owner.IsEffectiveVisible ||
                    owner.ChatInputArea.AttachmentInteractionVersion != _initialInteractionVersion ||
                    !owner.ViewModel.ContainsAttachment(attachment))
                {
                    Cancel();
                    return true;
                }

                if (_hasLocatedTarget && !owner.ChatInputArea.TryGetAttachmentBoundsOnScreen(attachment, out _))
                {
                    Cancel();
                    return true;
                }

                if (_acceptanceStarted && ShouldCheckOcclusion() &&
                    owner.ChatInputArea.TryGetAttachmentBoundsOnScreen(attachment, out var bounds) &&
                    owner._windowHelper.IsRegionCovered(owner, bounds) is false)
                {
                    Cancel();
                }

                return _isCancelled;
            }
        }

        public bool TryGetTargetCenterOnScreen(out PixelPoint point)
        {
            point = default;
            if (IsCancelled) return false;

            var result = owner.ChatInputArea.TryGetAttachmentBoundsOnScreen(attachment, out var bounds);
            _hasLocatedTarget |= result;
            if (result) point = bounds.Center;
            return result;
        }

        public bool BeginAcceptance()
        {
            if (IsCancelled) return false;
            if (_acceptanceStarted) return true;

            _acceptanceStarted = owner.ChatInputArea.BeginAttachmentAcceptance(attachment);
            if (_acceptanceStarted)
            {
                owner._logger.LogDebug("Visual element capture {OperationId} began target acceptance", operationId);
            }

            return _acceptanceStarted;
        }

        public bool IsReadyForHandoff(out PixelPoint point)
        {
            point = default;
            if (!_acceptanceStarted || IsCancelled ||
                !owner.ChatInputArea.IsAttachmentReady(attachment, out var bounds))
            {
                return false;
            }

            point = bounds.Center;
            return true;
        }

        public void BeginHandoff()
        {
            if (_handoffStarted || _isCancelled || IsCompleted) return;

            _handoffStarted = true;
            owner._logger.LogDebug("Visual element capture {OperationId} requested handoff", operationId);
            owner.ChatInputArea.CompleteAttachmentAcceptance(attachment);
            CompleteHandoffAfterRenderAsync().Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        public void Cancel()
        {
            if (_isCancelled || IsCompleted) return;

            _isCancelled = true;
            _handoffCancellation.Cancel();
            owner._logger.LogDebug("Visual element capture {OperationId} cancelled its presentation", operationId);
            owner.ChatInputArea.CancelAttachmentAcceptance(attachment);
            owner.OperationEnded(this);
        }

        private async Task CompleteHandoffAfterRenderAsync()
        {
            try
            {
                var visual = ElementComposition.GetElementVisual(owner);
                if (visual is null)
                {
                    await owner.Dispatcher.InvokeAsync(Cancel, DispatcherPriority.Render);
                    return;
                }

                await visual.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(_handoffCancellation.Token);
                await owner.Dispatcher.InvokeAsync(
                    () =>
                    {
                        if (IsCancelled || IsCompleted) return;
                        if (!owner.ViewModel.ContainsAttachment(attachment) ||
                            !owner.IsEffectiveVisible)
                        {
                            Cancel();
                            return;
                        }

                        IsCompleted = true;
                        owner._logger.LogDebug("Visual element capture {OperationId} completed rendered handoff", operationId);
                        owner.OperationEnded(this);
                    },
                    DispatcherPriority.Render);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                owner._logger.LogWarning(ex, "Visual element capture {OperationId} failed while waiting for rendered handoff", operationId);
                await owner.Dispatcher.InvokeAsync(Cancel, DispatcherPriority.Render);
            }
            finally
            {
                _handoffCancellation.Dispose();
            }
        }

        private bool ShouldCheckOcclusion()
        {
            var now = Environment.TickCount64;
            if (now - _lastOcclusionCheckMilliseconds < 50) return false;

            _lastOcclusionCheckMilliseconds = now;
            return true;
        }
    }
}