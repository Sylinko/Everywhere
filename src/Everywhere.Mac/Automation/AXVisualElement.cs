using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Mac.Interop;
using Everywhere.Utilities;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Represents one Context-owned macOS Accessibility element.
/// </summary>
public class AXVisualElement : VisualElement
{
    private static VisualElementMetadataKey<nint> ChildIndexMetadataKey { get; } = new("ax-children-index");

    private AXUIElement NativeElement => _nativeElement ?? throw new ObjectDisposedException(nameof(AXVisualElement));

    private MacVisualElementBackend Backend { get; }

    private AXUIElement? _nativeElement;

    private protected AXVisualElement(
        VisualElementIdentity identity,
        MacVisualElementBackend backend,
        AXUIElement nativeElement,
        string id
    ) : base(identity, id)
    {
        Backend = backend;
        _nativeElement = nativeElement;
    }

    internal static AXVisualElement Create(
        VisualElementIdentity identity,
        MacVisualElementBackend backend,
        AXUIElement operationElement,
        bool isSystemWide)
    {
        var nativeElement = operationElement.Retain();
        try
        {
            var id = backend.AllocateVisualElementId("ax");
            return isSystemWide ?
                new AXSystemWideVisualElement(identity, backend, nativeElement, id) :
                new AXVisualElement(identity, backend, nativeElement, id);
        }
        catch
        {
            nativeElement.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
    {
        var requestedFields = request.RequestedFields;
        var availableFields = VisualElementFields.None;
        var id = default(string);
        var type = default(VisualElementType?);
        var states = default(VisualElementStates?);
        var name = default(string);
        var text = default(string);
        var hasMoreText = false;
        var bounds = default(PixelRect?);
        var processId = default(int?);
        var nativeWindowHandle = default(nint?);
        var role = AXRoleAttribute.AXUnknown;
        var roleError = AXError.AttributeUnsupported;
        var failure = default(VisualElementQueryFailure);

        var attributes = GetRequestedAttributes(requestedFields);
        if (attributes.Count > 0)
        {
            using var batch = AXAttributeBatch.Copy(NativeElement, attributes);
            if (batch.Error.IsProviderFailure())
            {
                RecordProviderFailure(batch.Error, "copy the AX scalar attribute batch", ref failure);
            }
            else if (batch.Error == AXError.Success)
            {
                if (requestedFields.HasFlag(VisualElementFields.Type))
                {
                    roleError = ReadRole(batch, AXAttributeConstants.Role, AXRoleAttribute.AXUnknown, out role);
                    var subroleError = ReadRole(batch, AXAttributeConstants.Subrole, AXSubroleAttribute.AXUnknown, out var subrole);
                    var hasElementFailure = RecordBatchSlotElementFailure(roleError, "read the AX role batch result", ref failure);
                    hasElementFailure |= RecordBatchSlotElementFailure(subroleError, "read the AX subrole batch result", ref failure);
                    if (!hasElementFailure && roleError == AXError.Success)
                    {
                        type = ToVisualElementType(role, subroleError == AXError.Success ? subrole : AXSubroleAttribute.AXUnknown);
                        availableFields |= VisualElementFields.Type;
                    }
                }
                else if (requestedFields.HasFlag(VisualElementFields.NativeWindowHandle))
                {
                    roleError = ReadRole(batch, AXAttributeConstants.Role, AXRoleAttribute.AXUnknown, out role);
                    RecordBatchSlotElementFailure(roleError, "read the AX role before mapping a native window identifier", ref failure);
                }

                if (requestedFields.HasFlag(VisualElementFields.States))
                {
                    var enabledError = ReadBoolean(batch, AXAttributeConstants.Enabled, out var isEnabled);
                    var focusedError = ReadBoolean(batch, AXAttributeConstants.Focused, out var isFocused);
                    var hiddenError = ReadBoolean(batch, AXAttributeConstants.Hidden, out var isHidden);
                    var selectedError = ReadBoolean(batch, AXAttributeConstants.Selected, out var isSelected);
                    var subroleError = ReadRole(batch, AXAttributeConstants.Subrole, AXSubroleAttribute.AXUnknown, out var subrole);
                    var hasElementFailure = RecordBatchSlotElementFailure(enabledError, "read the AX enabled batch result", ref failure);
                    hasElementFailure |= RecordBatchSlotElementFailure(focusedError, "read the AX focused batch result", ref failure);
                    hasElementFailure |= RecordBatchSlotElementFailure(hiddenError, "read the AX hidden batch result", ref failure);
                    hasElementFailure |= RecordBatchSlotElementFailure(selectedError, "read the AX selected batch result", ref failure);
                    hasElementFailure |= RecordBatchSlotElementFailure(subroleError, "read the AX subrole batch result", ref failure);
                    var hasFieldFailure = !IsUsableOptionalBatchSlot(enabledError) ||
                        !IsUsableOptionalBatchSlot(focusedError) ||
                        !IsUsableOptionalBatchSlot(hiddenError) ||
                        !IsUsableOptionalBatchSlot(selectedError) ||
                        !IsUsableOptionalBatchSlot(subroleError);
                    if (!hasElementFailure && !hasFieldFailure)
                    {
                        var value = VisualElementStates.None;
                        if (isEnabled == false) value |= VisualElementStates.Disabled;
                        if (isFocused == true) value |= VisualElementStates.Focused;
                        if (isHidden == true) value |= VisualElementStates.Offscreen;
                        if (isSelected == true) value |= VisualElementStates.Selected;
                        if (subrole == AXSubroleAttribute.AXSecureTextField) value |= VisualElementStates.Password;
                        states = value;
                        availableFields |= VisualElementFields.States;
                    }
                }

                if (requestedFields.HasFlag(VisualElementFields.Name))
                {
                    var error = batch.GetString(AXAttributeConstants.Title, out name);
                    RecordBatchSlotElementFailure(error, "read the AX title batch result", ref failure);
                    if (error == AXError.Success)
                    {
                        availableFields |= VisualElementFields.Name;
                    }
                }

                if (requestedFields.HasFlag(VisualElementFields.Bounds))
                {
                    var positionError = batch.GetPoint(AXAttributeConstants.Position, out var position);
                    var sizeError = batch.GetSize(AXAttributeConstants.Size, out var size);
                    RecordBatchSlotElementFailure(positionError, "read the AX position batch result", ref failure);
                    RecordBatchSlotElementFailure(sizeError, "read the AX size batch result", ref failure);
                    if (positionError == AXError.Success && sizeError == AXError.Success && position is { } point && size is { } dimensions)
                    {
                        bounds = new PixelRect((int)point.X, (int)point.Y, (int)dimensions.Width, (int)dimensions.Height);
                        availableFields |= VisualElementFields.Bounds;
                    }
                }

                if (requestedFields.HasFlag(VisualElementFields.Text))
                {
                    var error = CopyBoundedText(batch, request.MaxTextCharacters, out text, out hasMoreText);
                    RecordBatchSlotElementFailure(error, "read the bounded AX text preview", ref failure);
                    if (error == AXError.Success)
                    {
                        availableFields |= VisualElementFields.Text;
                    }
                }
            }
        }

        if (failure is null && requestedFields.HasFlag(VisualElementFields.ProcessId))
        {
            var error = NativeElement.GetProcessId(out var value);
            RecordProviderFailure(error, "read the AX process identifier", ref failure);
            if (error == AXError.Success)
            {
                processId = value;
                availableFields |= VisualElementFields.ProcessId;
            }
        }

        if (failure is null && requestedFields.HasFlag(VisualElementFields.NativeWindowHandle) && roleError == AXError.Success &&
            role == AXRoleAttribute.AXWindow)
        {
            var error = NativeElement.GetNativeWindowHandle(out var value);
            RecordProviderFailure(error, "read the AX native window identifier", ref failure);
            if (error == AXError.Success)
            {
                nativeWindowHandle = (nint)value;
                availableFields |= VisualElementFields.NativeWindowHandle;
            }
        }

        if (requestedFields.HasFlag(VisualElementFields.Id))
        {
            id = Id;
            availableFields |= VisualElementFields.Id;
        }

        return new VisualElementQueryResult(
            this,
            new VisualElementSnapshot(id, type, states, name, text, hasMoreText, bounds, processId, nativeWindowHandle),
            availableFields,
            requestedFields & ~availableFields,
            failure);
    }

    /// <inheritdoc />
    protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters)
    {
        using var batch = AXAttributeBatch.Copy(NativeElement, [AXAttributeConstants.NumberOfCharacters]);
        var countError = batch.GetInt64(AXAttributeConstants.NumberOfCharacters, out var characterCount);
        if (countError == AXError.Success)
        {
            if (characterCount is not >= 0)
            {
                return CreateTextReadFailure(AXError.Failure, "read a valid AX character count");
            }

            if (offset >= characterCount.Value)
            {
                return new VisualElementTextReadResult(string.Empty, null, null);
            }

            // Request one UTF-16 code unit of lookahead so the shared page boundary rule can avoid
            // splitting a surrogate pair while still advancing offsets in the provider's native unit.
            var remainingCharacters = characterCount.Value - offset;
            var requestedCharacters = (nint)Math.Min(remainingCharacters, (long)maxCharacters + 1);
            var rangeError = NativeElement.CopyParameterizedStringAttribute(
                AXAttributeConstants.StringForRange,
                offset,
                requestedCharacters,
                out var rangedText);
            if (rangeError == AXError.Success && !string.IsNullOrEmpty(rangedText))
            {
                var localPage = VisualElementTextReadResult.FromSuccess(rangedText, 0, maxCharacters);
                var pageText = localPage.Text ?? string.Empty;
                if (pageText.Length == 0)
                {
                    return CreateTextReadFailure(AXError.Failure, "advance a valid UTF-16 AX text page");
                }

                var nextOffset = checked((long)offset + pageText.Length);
                return new VisualElementTextReadResult(
                    pageText,
                    nextOffset < characterCount.Value ? checked((int)nextOffset) : null,
                    null);
            }

            // Some providers report a positive character count and successfully return an empty AXStringForRange
            // while exposing the actual text through AXValue. Treat that as a non-progressing ranged capability and
            // use the same bounded AXValue fallback as providers that do not implement ranged access.
            if (rangeError != AXError.Success && !AllowsUnboundedTextFallback(rangeError) && rangeError != AXError.IllegalArgument)
            {
                return CreateTextReadFailure(rangeError, "read the requested AX text range");
            }
        }
        else if (!AllowsUnboundedTextFallback(countError))
        {
            return CreateTextReadFailure(countError, "read the AX character count");
        }

        // Providers without the ranged text attributes can still expose AXValue. This deliberately
        // mirrors the Windows fallback: repeated pages may recopy the full value, which can be
        // O(n²) over a complete traversal but keeps offsets simple and provider-neutral.
        var valueError = NativeElement.CopyDescriptionAttribute(AXAttributeConstants.Value, out var valueText);
        if (valueError == AXError.Success && valueText is not null)
        {
            return VisualElementTextReadResult.FromSuccess(valueText, offset, maxCharacters);
        }

        return CreateTextFallbackFailure(valueError, "read the AX value fallback");
    }

    /// <inheritdoc />
    protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request)
    {
        ValidateRelation(relation);
        var role = ReadNativeRole(NativeElement, "read the AX role before creating a relation Enumerator");
        if (role is AXRoleAttribute.AXApplication or AXRoleAttribute.AXSystemWide)
        {
            // AXApplication is a provider boundary, not part of the projected visual tree. AXSystemWide is
            // exposed only as the special Default + Direct query root and is intentionally non-traversable.
            return EmptyVisualElementEnumerator.Shared;
        }

        if (role != AXRoleAttribute.AXWindow)
        {
            return new NativeAXVisualElementEnumerator(this, relation, request);
        }

        var windowIdError = NativeElement.GetNativeWindowHandle(out var windowId);
        windowIdError.ThrowIfProviderFailure("map the AXWindow to its Quartz window identifier");
        if (windowIdError != AXError.Success || windowId == 0)
        {
            return relation == VisualElementRelation.Child ?
                new NativeAXVisualElementEnumerator(this, relation, request) :
                EmptyVisualElementEnumerator.Shared;
        }

        var topology = CGDisplayTopology.Current;
        return relation switch
        {
            VisualElementRelation.Parent => new WindowScreenEnumerator(
                Context,
                Backend,
                topology,
                windowId,
                request),
            VisualElementRelation.Child => new NativeAXVisualElementEnumerator(this, relation, request),
            VisualElementRelation.PreviousSibling or VisualElementRelation.NextSibling => TopLevelWindowEnumerator.CreateSiblings(
                Context,
                Backend,
                topology,
                windowId,
                relation,
                request),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
        };
    }

    /// <inheritdoc />
    protected override void InvokeCore() => NativeElement.PerformAction(AXAttributeConstants.Press);

    /// <inheritdoc />
    protected override void SetTextCore(string text) => NativeElement.SetText(text);

    /// <inheritdoc />
    protected override void FocusCore()
    {
        using var value = NSNumber.FromBoolean(true);
        var error = NativeElement.SetAttributeValueWithError(AXAttributeConstants.Focused, value);
        if (error != AXError.Success)
        {
            throw new AXException(error, $"Failed to focus the macOS Accessibility element. AX returned {error}.");
        }
    }

    /// <inheritdoc />
    protected override string? GetSelectedTextCore(int maxCharacters)
    {
        var text = NativeElement.GetSelectionText();
        return text is not null && text.Length > maxCharacters ? text[..maxCharacters] : text;
    }

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) =>
        NativeElement.CaptureAsync(cancellationToken);

    /// <inheritdoc />
    protected override bool TryConvertPlatformException(Exception exception, [NotNullWhen(true)] out Exception? convertedException)
    {
        if (exception is AXException axException)
        {
            convertedException = axException.CreateException();
            return true;
        }

        convertedException = null;
        return false;
    }

    /// <inheritdoc />
    protected override void ReleaseCore()
    {
        _nativeElement?.Dispose();
        _nativeElement = null;
    }

    private protected static void ValidateRelation(VisualElementRelation relation)
    {
        if (relation is < VisualElementRelation.Parent or > VisualElementRelation.NextSibling)
        {
            throw new ArgumentOutOfRangeException(nameof(relation), relation, null);
        }
    }

    private static IReadOnlyList<NSString> GetRequestedAttributes(VisualElementFields requestedFields)
    {
        var attributes = new List<NSString>(10);
        if (requestedFields.HasFlag(VisualElementFields.Type) || requestedFields.HasFlag(VisualElementFields.NativeWindowHandle))
        {
            AddAttribute(attributes, AXAttributeConstants.Role);
        }

        if (requestedFields.HasFlag(VisualElementFields.Type))
        {
            AddAttribute(attributes, AXAttributeConstants.Subrole);
        }

        if (requestedFields.HasFlag(VisualElementFields.States))
        {
            AddAttribute(attributes, AXAttributeConstants.Enabled);
            AddAttribute(attributes, AXAttributeConstants.Focused);
            AddAttribute(attributes, AXAttributeConstants.Hidden);
            AddAttribute(attributes, AXAttributeConstants.Selected);
            AddAttribute(attributes, AXAttributeConstants.Subrole);
        }

        if (requestedFields.HasFlag(VisualElementFields.Name)) AddAttribute(attributes, AXAttributeConstants.Title);
        if (requestedFields.HasFlag(VisualElementFields.Bounds))
        {
            AddAttribute(attributes, AXAttributeConstants.Position);
            AddAttribute(attributes, AXAttributeConstants.Size);
        }

        // AXValue can be arbitrarily large. Ask only for its bounded-access metadata in the scalar batch;
        // CopyBoundedText uses AXStringForRange and falls back to AXValue only for providers without range access.
        if (requestedFields.HasFlag(VisualElementFields.Text)) AddAttribute(attributes, AXAttributeConstants.NumberOfCharacters);
        return attributes;
    }

    private static void AddAttribute(List<NSString> attributes, NSString attribute)
    {
        if (attributes.All(existing => existing.Handle.Handle != attribute.Handle.Handle))
        {
            attributes.Add(attribute);
        }
    }

    private static AXError ReadRole<TEnum>(AXAttributeBatch batch, NSString attribute, TEnum fallback, out TEnum role) where TEnum : struct, Enum
    {
        var error = batch.GetString(attribute, out var nativeRole);
        role = Enum.TryParse<TEnum>(nativeRole, true, out var parsed) ? parsed : fallback;
        return error;
    }

    private static AXError ReadBoolean(AXAttributeBatch batch, NSString attribute, out bool? value)
    {
        return batch.GetBoolean(attribute, out value);
    }

    private AXError CopyBoundedText(AXAttributeBatch batch, int maximumCharacters, out string? text, out bool hasMoreText)
    {
        text = null;
        hasMoreText = false;
        var countError = batch.GetInt64(AXAttributeConstants.NumberOfCharacters, out var characterCount);
        if (maximumCharacters == 0)
        {
            if (countError == AXError.Success && characterCount is >= 0)
            {
                text = string.Empty;
                hasMoreText = characterCount > 0;
                return AXError.Success;
            }

            return countError == AXError.Success ? AXError.Failure : countError;
        }

        if (countError == AXError.Success)
        {
            if (characterCount is not >= 0)
            {
                return AXError.Failure;
            }

            var count = characterCount.Value;
            var requestedLength = checked((nint)Math.Min(count, maximumCharacters));
            hasMoreText = count > requestedLength;
            if (requestedLength == 0)
            {
                text = string.Empty;
                return AXError.Success;
            }

            var rangeError = NativeElement.CopyParameterizedStringAttribute(
                AXAttributeConstants.StringForRange,
                0,
                requestedLength,
                out text);
            if (rangeError == AXError.Success || !AllowsUnboundedTextFallback(rangeError))
            {
                return rangeError;
            }
        }
        else if (!AllowsUnboundedTextFallback(countError))
        {
            return countError;
        }

        var valueError = NativeElement.CopyDescriptionAttribute(AXAttributeConstants.Value, out text);
        if (valueError == AXError.Success && text is not null && text.Length > maximumCharacters)
        {
            hasMoreText = true;
            text = text[..maximumCharacters];
        }

        return valueError;
    }

    private static bool AllowsUnboundedTextFallback(AXError error) => error is
        AXError.AttributeUnsupported or
        AXError.ParameterizedAttributeUnsupported or
        AXError.NotImplemented or
        AXError.NoValue;

    private static VisualElementTextReadResult CreateTextReadFailure(AXError error, string operation) =>
        VisualElementTextReadResult.FromFailure(new AXException(error, $"Failed to {operation}. AX returned {error}.").CreateFailure());

    private static VisualElementTextReadResult CreateTextFallbackFailure(AXError error, string operation)
    {
        var exception = new AXException(error, $"Failed to {operation}. AX returned {error}.");
        // Some providers advertise AXValue on non-text elements but return the generic Failure code when no
        // value can be produced. Normalize only this optional fallback as unsupported; the original AXException
        // remains available to diagnostics, while generic failures from whole transactions remain provider failures.
        return error == AXError.Failure ?
            VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null, exception)) :
            VisualElementTextReadResult.FromFailure(exception.CreateFailure());
    }

    private static bool IsUsableOptionalBatchSlot(AXError error) => error == AXError.Success || error.IsUnsupported();

    private static bool RecordBatchSlotElementFailure(AXError error, string operation, ref VisualElementQueryFailure? failure)
    {
        // A successful CopyMultipleAttributeValues call proves that the element transaction completed. Providers
        // may still return generic Failure, IllegalArgument, or a malformed value for one advertised attribute;
        // those are field-local omissions, not the provider-wide failure represented by QueryResult.Failure.
        if (error is not (AXError.InvalidUIElement or AXError.CannotComplete or AXError.APIDisabled))
        {
            return false;
        }

        return RecordProviderFailure(error, operation, ref failure);
    }

    private static bool RecordProviderFailure(AXError error, string operation, ref VisualElementQueryFailure? failure)
    {
        if (!error.IsProviderFailure())
        {
            return false;
        }

        failure ??= new AXException(error, $"Failed to {operation}. AX returned {error}.").CreateFailure();
        return true;
    }

    private static VisualElementType ToVisualElementType(AXRoleAttribute role, AXSubroleAttribute subrole)
    {
        return role switch
        {
            AXRoleAttribute.AXStaticText => VisualElementType.Label,
            AXRoleAttribute.AXTextField or AXRoleAttribute.AXTextArea => VisualElementType.TextEdit,
            AXRoleAttribute.AXButton or
                AXRoleAttribute.AXMenuButton or
                AXRoleAttribute.AXPopUpButton or
                AXRoleAttribute.AXDisclosureTriangle => VisualElementType.Button,
            AXRoleAttribute.AXCheckBox => VisualElementType.CheckBox,
            AXRoleAttribute.AXRadioButton => VisualElementType.RadioButton,
            AXRoleAttribute.AXComboBox => VisualElementType.ComboBox,
            AXRoleAttribute.AXList or AXRoleAttribute.AXRuler => VisualElementType.ListView,
            AXRoleAttribute.AXOutline => VisualElementType.TreeView,
            AXRoleAttribute.AXTable => VisualElementType.Table,
            AXRoleAttribute.AXRow => VisualElementType.TableRow,
            AXRoleAttribute.AXMenuBar or AXRoleAttribute.AXMenu => VisualElementType.Menu,
            AXRoleAttribute.AXMenuBarItem or AXRoleAttribute.AXMenuItem => VisualElementType.MenuItem,
            AXRoleAttribute.AXTabGroup => VisualElementType.TabControl,
            AXRoleAttribute.AXToolbar => VisualElementType.ToolBar,
            AXRoleAttribute.AXWindow => VisualElementType.TopLevel,
            AXRoleAttribute.AXSplitter => VisualElementType.Splitter,
            AXRoleAttribute.AXSlider => VisualElementType.Slider,
            AXRoleAttribute.AXScrollBar => VisualElementType.ScrollBar,
            AXRoleAttribute.AXBusyIndicator => VisualElementType.Spinner,
            AXRoleAttribute.AXProgressIndicator or
                AXRoleAttribute.AXLevelIndicator or
                AXRoleAttribute.AXRelevanceIndicator or
                AXRoleAttribute.AXValueIndicator => VisualElementType.ProgressBar,
            AXRoleAttribute.AXImage => VisualElementType.Image,
            AXRoleAttribute.AXLink => VisualElementType.Hyperlink,
            AXRoleAttribute.AXWebArea => VisualElementType.Document,
            AXRoleAttribute.AXGroup or AXRoleAttribute.AXRadioGroup or AXRoleAttribute.AXSplitGroup or AXRoleAttribute.AXBrowser or
                AXRoleAttribute.AXSheet or AXRoleAttribute.AXDrawer or AXRoleAttribute.AXCell or AXRoleAttribute.AXScrollArea or
                AXRoleAttribute.AXLayoutArea or AXRoleAttribute.AXLayoutItem or AXRoleAttribute.AXGrowArea or AXRoleAttribute.AXMatte or
                AXRoleAttribute.AXRulerMarker or AXRoleAttribute.AXColumn or AXRoleAttribute.AXGrid or AXRoleAttribute.AXPage or
                AXRoleAttribute.AXPopover => VisualElementType.Panel,
            _ => subrole switch
            {
                AXSubroleAttribute.AXCloseButton or AXSubroleAttribute.AXMinimizeButton or AXSubroleAttribute.AXZoomButton or
                    AXSubroleAttribute.AXToolbarButton or AXSubroleAttribute.AXSortButton or
                    AXSubroleAttribute.AXTabButton => VisualElementType.Button,
                AXSubroleAttribute.AXSearchField => VisualElementType.TextEdit,
                AXSubroleAttribute.AXToggle or AXSubroleAttribute.AXSwitch => VisualElementType.CheckBox,
                AXSubroleAttribute.AXStandardWindow or AXSubroleAttribute.AXDialog or AXSubroleAttribute.AXSystemDialog or
                    AXSubroleAttribute.AXFloatingWindow or AXSubroleAttribute.AXSystemFloatingWindow => VisualElementType.Panel,
                _ => VisualElementType.Unknown,
            },
        };
    }

    private static AXRoleAttribute ReadNativeRole(AXUIElement element, string operation)
    {
        var error = element.CopyStringAttribute(AXAttributeConstants.Role, out var nativeRole);
        error.ThrowIfProviderFailure(operation);
        return error == AXError.Success && Enum.TryParse<AXRoleAttribute>(nativeRole, true, out var role) ?
            role :
            AXRoleAttribute.AXUnknown;
    }

    private sealed class NativeAXVisualElementEnumerator : IVisualElementEnumerator
    {
        public VisualElementQueryResult Current
        {
            get
            {
                ThrowIfUnavailable();
                return _current ?? throw new InvalidOperationException("The Enumerator has no current item.");
            }
        }

        object IEnumerator.Current => Current;

        public int Count => -1;

        public int Index { get; private set; } = -1;

        private const int MaximumSiblingSearchCount = 4096;
        private const int ChildPageSize = 64;

        private AXVisualElement? _origin;
        private readonly VisualElementRetention _retention;
        private readonly VisualElementRelation _relation;
        private readonly VisualElementQueryRequest _queryRequest;

        private AXUIElement? _siblingParent;
        private NSArray? _childPage;
        private VisualElementQueryResult? _lookahead;
        private VisualElementQueryResult? _current;
        private nint _childCount = -1;
        private nint _childPageStart = -1;
        private nint _nextNativeIndex;
        private int _nextResultIndex;
        private int _direction = 1;
        private bool _isInitialized;
        private bool _isLookaheadResolved;
        private bool _isCompleted;
        private bool _isDisposed;

        internal NativeAXVisualElementEnumerator(AXVisualElement origin, VisualElementRelation relation, VisualElementQueryRequest queryRequest)
        {
            _origin = origin;
            _relation = relation;
            _queryRequest = queryRequest;
            _retention = origin.Context.CreateRetention();
            _retention.Retain(origin);
        }

        public bool HasMore
        {
            get
            {
                ThrowIfUnavailable();
                EnsureLookahead();
                return _lookahead is not null;
            }
        }

        public bool MoveNext()
        {
            ThrowIfUnavailable();
            EnsureLookahead();
            if (_lookahead is not { } next)
            {
                _current = null;
                Index = -1;
                return false;
            }

            _lookahead = null;
            _isLookaheadResolved = false;
            _current = next;
            Index = _nextResultIndex++;
            if (_relation == VisualElementRelation.Parent)
            {
                _isCompleted = true;
            }
            else
            {
                _nextNativeIndex += _direction;
            }

            return true;
        }

        public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _origin = null;
            _lookahead = null;
            _current = null;
            DisposeHelper.DisposeToDefault(ref _childPage);
            DisposeHelper.DisposeToDefault(ref _siblingParent);
            _retention.Dispose();
        }

        private void EnsureLookahead()
        {
            if (_isLookaheadResolved || _isCompleted)
            {
                return;
            }

            try
            {
                Initialize();
                if (_isCompleted)
                {
                    return;
                }

                _lookahead = QueryNext();
                _isLookaheadResolved = true;
                _isCompleted = _lookahead is null;
            }
            catch (AXException exception)
            {
                throw exception.CreateException();
            }
        }

        private void Initialize()
        {
            if (_isInitialized)
            {
                return;
            }

            _isInitialized = true;
            switch (_relation)
            {
                case VisualElementRelation.Parent:
                    break;
                case VisualElementRelation.Child:
                    _nextNativeIndex = 0;
                    break;
                case VisualElementRelation.PreviousSibling:
                    _direction = -1;
                    InitializeSiblingIndex();
                    break;
                case VisualElementRelation.NextSibling:
                    InitializeSiblingIndex();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_relation), _relation, null);
            }
        }

        private VisualElementQueryResult? QueryNext()
        {
            var origin = _origin ?? throw new ObjectDisposedException(nameof(NativeAXVisualElementEnumerator));
            var nativeIndex = _nextNativeIndex;
            using var nativeElement = _relation switch
            {
                VisualElementRelation.Parent => CopyProjectedParent(origin),
                VisualElementRelation.Child => CopyChildFromPage(origin.NativeElement, nativeIndex),
                VisualElementRelation.PreviousSibling or VisualElementRelation.NextSibling => CopyChildFromPage(_siblingParent, nativeIndex),
                _ => throw new ArgumentOutOfRangeException(nameof(_relation), _relation, null),
            };
            if (nativeElement is null)
            {
                return null;
            }

            var element = origin.Backend.GetOrCreateAXElement(_retention, nativeElement);
            if (_relation != VisualElementRelation.Parent)
            {
                element.Metadata.Set(ChildIndexMetadataKey, nativeIndex);
            }

            return element.Query(_queryRequest);
        }

        private void InitializeSiblingIndex()
        {
            var origin = _origin ?? throw new ObjectDisposedException(nameof(NativeAXVisualElementEnumerator));
            _siblingParent = CopyProjectedParent(origin);
            if (_siblingParent is null)
            {
                _isCompleted = true;
                return;
            }

            if (TryInitializeSiblingIndexFromNative(origin) || TryInitializeSiblingIndexFromMetadata(origin))
            {
                return;
            }

            InitializeSiblingIndexByScanning(origin, true);
        }

        private bool TryInitializeSiblingIndexFromNative(AXVisualElement origin)
        {
            // The parameterized index attribute was introduced in macOS 26. Calling it directly also
            // probes support for this particular provider without a separate supported-names RPC.
            if (!OperatingSystem.IsMacOSVersionAtLeast(26))
            {
                return false;
            }

            var siblingParent = _siblingParent ?? throw new InvalidOperationException("AX sibling lookup requires a parent element.");
            var error = siblingParent.CopyParameterizedInt64Attribute(
                AXAttributeConstants.IndexForChildUIElement,
                origin.NativeElement,
                out var childIndex);
            error.ThrowIfProviderFailure("locate an AX child through the native index attribute");
            if (error != AXError.Success)
            {
                return false;
            }

            if (childIndex is not { } index || index < 0)
            {
                origin.Metadata.Remove(ChildIndexMetadataKey);
                _isCompleted = true;
                return true;
            }

            var nativeIndex = checked((nint)index);
            origin.Metadata.Set(ChildIndexMetadataKey, nativeIndex);
            _nextNativeIndex = checked(nativeIndex + _direction);
            return true;
        }

        private bool TryInitializeSiblingIndexFromMetadata(AXVisualElement origin)
        {
            if (!origin.Metadata.TryGetValue(ChildIndexMetadataKey, out var childIndex) || childIndex < 0)
            {
                return false;
            }

            using var candidate = CopyChildFromPage(_siblingParent, childIndex);
            if (candidate is null || !CFInterop.CFEqual(origin.NativeElement.NativeHandle, candidate.NativeHandle))
            {
                origin.Metadata.Remove(ChildIndexMetadataKey);
                return false;
            }

            _nextNativeIndex = checked(childIndex + _direction);
            return true;
        }

        private void InitializeSiblingIndexByScanning(AXVisualElement origin, bool shouldRetryAfterRangeChange)
        {
            var siblingParent = _siblingParent ?? throw new InvalidOperationException("AX sibling lookup requires a parent element.");
            if (!TryGetChildCount(siblingParent, out var count))
            {
                _isCompleted = true;
                return;
            }

            var searchCount = Math.Min(count, MaximumSiblingSearchCount);
            for (nint pageStart = 0; pageStart < searchCount; pageStart += ChildPageSize)
            {
                var pageLength = Math.Min(ChildPageSize, searchCount - pageStart);
                var error = siblingParent.CopyAttributeValues(AXAttributeConstants.Children, pageStart, pageLength, out var values);
                using (values)
                {
                    if (error == AXError.IllegalArgument && shouldRetryAfterRangeChange)
                    {
                        InvalidateChildPage();
                        _childCount = -1;
                        InitializeSiblingIndexByScanning(origin, false);
                        return;
                    }

                    error.ThrowIfProviderFailure("copy an AX sibling page");
                    if (error != AXError.Success || values is null)
                    {
                        _isCompleted = true;
                        return;
                    }

                    for (nuint pageIndex = 0; pageIndex < values.Count; pageIndex++)
                    {
                        var candidate = values.ValueAt(pageIndex).Handle;
                        if (candidate == 0 || !CFInterop.CFEqual(origin.NativeElement.NativeHandle, candidate))
                        {
                            continue;
                        }

                        var nativeIndex = pageStart + (nint)pageIndex;
                        origin.Metadata.Set(ChildIndexMetadataKey, nativeIndex);
                        _nextNativeIndex = checked(nativeIndex + _direction);
                        return;
                    }
                }
            }

            if (count > MaximumSiblingSearchCount)
            {
                throw new InvalidOperationException($"AX sibling lookup exceeded the {MaximumSiblingSearchCount}-element search limit.");
            }

            _isCompleted = true;
        }

        private bool TryGetChildCount(AXUIElement? parent, out nint count)
        {
            if (parent is null)
            {
                count = 0;
                return false;
            }

            if (_childCount >= 0)
            {
                count = _childCount;
                return true;
            }

            var error = parent.GetAttributeValueCount(AXAttributeConstants.Children, out count);
            error.ThrowIfProviderFailure("count AX children");
            if (error != AXError.Success)
            {
                _childCount = 0;
                return false;
            }

            _childCount = count;
            return true;
        }

        private static AXUIElement? CopyProjectedParent(AXVisualElement origin)
        {
            var parent = origin.NativeElement.GetAttributeAsElement(AXAttributeConstants.Parent, out var error);
            error.ThrowIfProviderFailure("copy the AX parent");
            if (parent is null)
            {
                return null;
            }

            try
            {
                if (ReadNativeRole(parent, "read the AX parent role") != AXRoleAttribute.AXApplication)
                {
                    return parent;
                }

                parent.Dispose();
                return null;
            }
            catch
            {
                parent.Dispose();
                throw;
            }
        }

        private AXUIElement? CopyChildFromPage(AXUIElement? parent, nint index, bool shouldRetryAfterRangeChange = true)
        {
            if (parent is null || index < 0)
            {
                return null;
            }

            if (!TryGetChildCount(parent, out var childCount))
            {
                return null;
            }

            if (index >= childCount)
            {
                return null;
            }

            var pageOffset = index - _childPageStart;
            if (_childPage is null || pageOffset < 0 || (nuint)pageOffset >= _childPage.Count)
            {
                InvalidateChildPage();
                _childPageStart = index - index % ChildPageSize;
                var pageLength = Math.Min(ChildPageSize, childCount - _childPageStart);
                var error = parent.CopyAttributeValues(
                    AXAttributeConstants.Children,
                    _childPageStart,
                    pageLength,
                    out _childPage);
                if (error == AXError.IllegalArgument && shouldRetryAfterRangeChange)
                {
                    InvalidateChildPage();
                    _childCount = -1;
                    return CopyChildFromPage(parent, index, false);
                }

                error.ThrowIfProviderFailure("copy an AX child page");
                if (error != AXError.Success || _childPage is null)
                {
                    _childPageStart = -1;
                    return null;
                }

                pageOffset = index - _childPageStart;
            }

            return pageOffset >= 0 && (nuint)pageOffset < _childPage.Count ? AXUIElement.FromArray(_childPage, (nuint)pageOffset) : null;
        }

        private void InvalidateChildPage()
        {
            DisposeHelper.DisposeToDefault(ref _childPage);
            _childPageStart = -1;
        }

        private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}