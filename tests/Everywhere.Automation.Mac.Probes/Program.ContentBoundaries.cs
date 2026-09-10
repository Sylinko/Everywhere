using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;
using Foundation;
using ObjCRuntime;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private const int ContentProbeTextLimit = 257;
    private const int ContentProbePageSize = 64;

    private static ContentBoundariesObservation ProbeContentBoundaries()
    {
        using var provider = StartProvider();
        var errorOutputTask = provider.StandardError.ReadToEndAsync();
        try
        {
            var providerProcessId = ReadProviderProcessId(provider);
            var contentStatus = SendProviderCommandAndRead(provider, "prepare-content", "CONTENT", 4);
            var contentWindowId = ParseWindowId(contentStatus[1], "content");
            var expectedTextLength = int.Parse(contentStatus[2]);
            var expectedChildCount = int.Parse(contentStatus[3]);
            Require(expectedTextLength > ContentProbeTextLimit, "The content probe text is not large enough to exercise ranged access.");
            Require(expectedChildCount > ContentProbePageSize, "The content probe collection does not cross an AX child page boundary.");

            using var nativeApplication = AXUIElement.ElementFromPid(providerProcessId) ??
                throw new InvalidOperationException("Could not create the content-probe AX application.");
            SendProviderCommandAndRead(provider, "focus-large-text", "FOCUSED_TEXT", 2);
            using var nativeTextElement = WaitForFocusedNativeElement(nativeApplication, expectedValuePrefix: "Everywhere-AX-range-");

            using var numberOfCharactersAttribute = new NSString("AXNumberOfCharacters");
            using var stringForRangeAttribute = new NSString("AXStringForRange");
            var countError = CopyIntegerAttribute(nativeTextElement, numberOfCharactersAttribute, out var nativeTextLength);
            Require(countError == AXError.Success, $"AXNumberOfCharacters returned {countError} for the controlled NSTextView.");
            Require(nativeTextLength == expectedTextLength, $"AXNumberOfCharacters returned {nativeTextLength} instead of {expectedTextLength}.");

            var valueError = CopyStringAttributeRaw(nativeTextElement, AXAttributeConstants.Value, out var completeText);
            Require(valueError == AXError.Success && completeText?.Length == expectedTextLength,
                $"The controlled NSTextView AXValue returned {valueError} and length {completeText?.Length.ToString() ?? "null"}.");
            var rangeError = CopyParameterizedStringAttributeRaw(
                nativeTextElement,
                stringForRangeAttribute,
                0,
                ContentProbeTextLimit,
                out var rangedText);
            Require(rangeError == AXError.Success, $"AXStringForRange returned {rangeError} for the controlled NSTextView.");
            Require(rangedText is not null && rangedText.Length == ContentProbeTextLimit,
                $"AXStringForRange returned {rangedText?.Length.ToString() ?? "null"} characters instead of {ContentProbeTextLimit}.");
            Require(completeText is not null && rangedText == completeText[..ContentProbeTextLimit], "AXStringForRange did not match the AXValue prefix.");
            var completeTextValue = completeText ?? throw new InvalidOperationException("The controlled NSTextView returned no AXValue text.");
            var rangedTextValue = rangedText ?? throw new InvalidOperationException("The controlled NSTextView returned no ranged text.");

            using var backend = new MacVisualElementBackend();
            using var context = new VisualContext();
            using var retention = context.CreateRetention();
            var textElement = GetOrCreateAXElement(backend, retention, nativeTextElement);
            var textRequest = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Text, ContentProbeTextLimit);
            var textResult = textElement.Query(textRequest);
            Require(textResult.IsSuccess, "The controlled large-text production query returned a provider failure.");
            Require(textResult.Snapshot.Type == VisualElementType.TextEdit, $"The controlled NSTextView mapped to {textResult.Snapshot.Type} instead of TextEdit.");
            Require(textResult.Snapshot.TextPreview == rangedTextValue, "The production text preview did not match the native ranged prefix.");
            var emptyTextResult = textElement.Query(new VisualElementQueryRequest(VisualElementFields.Text, 0));
            Require(emptyTextResult.IsSuccess && emptyTextResult.Snapshot.TextPreview == string.Empty,
                "A zero-character production text request did not return an empty bounded preview.");

            var firstTextPage = textElement.ReadText(0, ContentProbeTextLimit);
            Require(firstTextPage == VisualElementTextReadResult.FromSuccess(completeTextValue, 0, ContentProbeTextLimit),
                "The first production text page did not match the controlled AXValue.");
            const int nonzeroTextOffset = 250;
            var nonzeroTextPage = textElement.ReadText(nonzeroTextOffset, ContentProbeTextLimit);
            Require(nonzeroTextPage == VisualElementTextReadResult.FromSuccess(completeTextValue, nonzeroTextOffset, ContentProbeTextLimit),
                "The nonzero production text page did not match the controlled AXValue.");
            var surrogateOffset = completeTextValue.IndexOf("🙂", StringComparison.Ordinal);
            Require(surrogateOffset >= 0, "The controlled AX text did not contain its expected surrogate pair.");
            var surrogateTextPage = textElement.ReadText(surrogateOffset, 1);
            Require(surrogateTextPage == VisualElementTextReadResult.FromSuccess(completeTextValue, surrogateOffset, 1),
                "The production text reader split an automatically generated UTF-16 surrogate pair.");
            var finalTextOffset = completeTextValue.Length - 3;
            var finalTextPage = textElement.ReadText(finalTextOffset, ContentProbeTextLimit);
            Require(finalTextPage == VisualElementTextReadResult.FromSuccess(completeTextValue, finalTextOffset, ContentProbeTextLimit),
                "The final production text page did not terminate at the controlled AXValue boundary.");
            var exhaustedTextPage = textElement.ReadText(completeTextValue.Length, ContentProbeTextLimit);
            Require(exhaustedTextPage == new VisualElementTextReadResult(string.Empty, null, null),
                "Reading at the controlled AX text length did not return an exhausted page.");
            var combinedText = new StringBuilder(completeTextValue.Length);
            var textPageCount = 0;
            var textOffset = 0;
            var textPagingStopwatch = Stopwatch.StartNew();
            for (; textPageCount < 512; textPageCount++)
            {
                var page = textElement.ReadText(textOffset, ContentProbeTextLimit);
                Require(page.Failure is null && page.Text is not null, $"Production text page {textPageCount} failed.");
                combinedText.Append(page.Text);
                if (page.NextOffset is not { } nextOffset)
                {
                    textPageCount++;
                    break;
                }

                Require(nextOffset > textOffset, $"Production text page {textPageCount} did not advance its offset.");
                textOffset = nextOffset;
            }
            textPagingStopwatch.Stop();
            Require(textPageCount < 512, "Production text paging did not terminate within its safety limit.");
            Require(combinedText.ToString() == completeTextValue, "Sequential production text pages did not reconstruct the controlled AXValue.");

            SendProviderCommandAndRead(provider, "focus-collection", "FOCUSED_COLLECTION", 2);
            using var nativeCollectionItem = WaitForFocusedNativeElement(nativeApplication, expectedTitle: "Item 065");
            using var nativeCollection = GetAttributeAsElement(nativeCollectionItem, AXAttributeConstants.Parent, out var parentError) ??
                throw new InvalidOperationException($"Could not copy the controlled collection parent: {parentError}.");
            var collectionItemElement = GetOrCreateAXElement(backend, retention, nativeCollectionItem);
            Require(collectionItemElement.Metadata.Count == 0,
                "The focused collection item unexpectedly had metadata before its parent was enumerated.");
            var childCountError = GetAttributeValueCountForContent(nativeCollection, AXAttributeConstants.Children, out var nativeChildCount);
            Require(childCountError == AXError.Success, $"Counting the controlled AXChildren returned {childCountError}.");
            Require(nativeChildCount == expectedChildCount, $"The controlled collection exposed {nativeChildCount} children instead of {expectedChildCount}.");

            var pageError = CopyAttributeValuesForContent(nativeCollection, AXAttributeConstants.Children, 0, ContentProbePageSize, out var firstPage);
            using (firstPage)
            {
                Require(pageError == AXError.Success && firstPage?.Count == ContentProbePageSize,
                    $"The first controlled AXChildren page returned {pageError} and {firstPage?.Count.ToString() ?? "null"} items.");
            }

            var collectionElement = GetOrCreateAXElement(backend, retention, nativeCollection);
            var fallbackCountError = CopyIntegerAttribute(nativeCollection, AXAttributeConstants.NumberOfCharacters, out _);
            Require(fallbackCountError is AXError.AttributeUnsupported or AXError.NoValue or AXError.NotImplemented,
                $"The controlled AXValue-only group unexpectedly exposed a character count: {fallbackCountError}.");
            const string fallbackText = "collection-fallback-text";
            var fallbackTextPage = collectionElement.ReadText(11, 8);
            Require(fallbackTextPage == VisualElementTextReadResult.FromSuccess(fallbackText, 11, 8),
                "The production text reader did not page the controlled AXValue fallback.");
            var childRequest = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Name, 0);
            var initialNames = EnumerateChildNames(collectionElement, childRequest, expectedChildCount + 1);
            Require(initialNames.Count == expectedChildCount, $"The production Enumerator returned {initialNames.Count} children instead of {expectedChildCount}.");
            Require(initialNames[0] == "Item 000" && initialNames[^1] == "Item 129", "The initial production child order did not preserve AppKit subview order.");
            Require(initialNames.Distinct(StringComparer.Ordinal).Count() == initialNames.Count, "The initial production child enumeration returned duplicate items.");
            Require(collectionItemElement.Metadata.Count > 0,
                "Enumerating the collection did not attach a child-position hint to the preexisting canonical focused item.");
            var initialNextSibling = ReadFirstRelatedName(collectionItemElement, VisualElementRelation.NextSibling, childRequest);
            Require(initialNextSibling == "Item 066", $"The initial next sibling was {initialNextSibling ?? "null"} instead of Item 066.");

            using var mutatingEnumerator = collectionElement.CreateEnumerator(
                VisualElementRelation.Child,
                childRequest);
            var mutatedNames = new List<string>();
            for (var index = 0; index < 70; index++)
            {
                Require(mutatingEnumerator.MoveNext(), $"The mutating Enumerator ended before item {index}.");
                mutatedNames.Add(mutatingEnumerator.Current.Snapshot.Name ?? string.Empty);
            }

            var retainedPageError = CopyAttributeValuesForContent(
                nativeCollection,
                AXAttributeConstants.Children,
                ContentProbePageSize,
                ContentProbePageSize,
                out var retainedPage);
            Require(retainedPageError == AXError.Success && retainedPage is { Count: ContentProbePageSize },
                $"The retained AXChildren page returned {retainedPageError} and {retainedPage?.Count.ToString() ?? "null"} items.");
            var requiredRetainedPage = retainedPage ?? throw new InvalidOperationException("The retained AXChildren page was null.");
            using (requiredRetainedPage)
            {
                SendProviderCommandAndRead(provider, "mutate-collection", "MUTATED_COLLECTION", 3);
                using var retainedItem = CreateAXElementFromArray(requiredRetainedPage, 6);
                var retainedTitleError = CopyStringAttributeForContent(retainedItem, AXAttributeConstants.Title, out var retainedTitle);
                Require(retainedTitleError == AXError.Success && retainedTitle == "Item 070",
                    $"A retained pre-mutation AXChildren page did not preserve item 70: {retainedTitleError}, {retainedTitle ?? "null"}.");

                while (mutatingEnumerator.MoveNext())
                {
                    if (mutatedNames.Count > expectedChildCount + 32)
                    {
                        throw new InvalidOperationException("The mutating production Enumerator did not remain bounded.");
                    }

                    mutatedNames.Add(mutatingEnumerator.Current.Snapshot.Name ?? string.Empty);
                }
            }

            Require(mutatedNames.Count == expectedChildCount,
                $"The mutating production Enumerator returned {mutatedNames.Count} items instead of its initial {expectedChildCount}-item bound.");
            Require(mutatedNames.Contains("Item 070", StringComparer.Ordinal),
                "The mutating production Enumerator discarded an element that was retained in its current operation-local page.");
            Require(mutatedNames.Distinct(StringComparer.Ordinal).Count() == mutatedNames.Count,
                "The mutating production Enumerator duplicated a Context-owned child.");
            var nextSiblingAfterMutation = ReadFirstRelatedName(collectionItemElement, VisualElementRelation.NextSibling, childRequest);
            Require(nextSiblingAfterMutation == "Item 066",
                $"The next sibling after invalidating the cached child index was {nextSiblingAfterMutation ?? "null"} instead of Item 066.");
            var previousSiblingAfterMutation = ReadFirstRelatedName(collectionItemElement, VisualElementRelation.PreviousSibling, childRequest);
            Require(previousSiblingAfterMutation == "Item 064",
                $"The previous sibling after refreshing the child index was {previousSiblingAfterMutation ?? "null"} instead of Item 064.");

            SendProviderCommandAndRead(provider, "close-content", "CLOSED_CONTENT", 2);
            var destroyedTextObservation = ObserveDestroyedElement(textElement, textRequest);
            var isClosedContentWindowUnresolvable = WaitForNativeWindowToDisappear(backend, context, contentWindowId, textRequest);
            Require(isClosedContentWindowUnresolvable, "The closed content window remained resolvable through a new NativeWindow query.");

            return new ContentBoundariesObservation(
                ProviderProcessId: providerProcessId,
                ContentWindowId: contentWindowId,
                NativeTextLength: nativeTextLength,
                CompleteValueLength: completeTextValue.Length,
                RangedTextLength: rangedTextValue.Length,
                RangedTextError: rangeError,
                ProductionTextLength: textResult.Snapshot.TextPreview?.Length ?? 0,
                FirstTextPageLength: firstTextPage.Text?.Length ?? 0,
                NonzeroTextPageOffset: nonzeroTextOffset,
                NonzeroTextPageLength: nonzeroTextPage.Text?.Length ?? 0,
                SurrogateTextPageLength: surrogateTextPage.Text?.Length ?? 0,
                FinalTextPageLength: finalTextPage.Text?.Length ?? 0,
                FallbackCountError: fallbackCountError,
                FallbackTextPageLength: fallbackTextPage.Text?.Length ?? 0,
                TextPageCount: textPageCount,
                TextPagingElapsedMilliseconds: textPagingStopwatch.Elapsed.TotalMilliseconds,
                NativeChildCount: checked((int)nativeChildCount),
                NativeFirstPageCount: ContentProbePageSize,
                InitialEnumeratedChildCount: initialNames.Count,
                MutatedEnumeratedChildCount: mutatedNames.Count,
                DidMutationPreserveBufferedItem70: mutatedNames.Contains("Item 070", StringComparer.Ordinal),
                InitialNextSibling: initialNextSibling,
                NextSiblingAfterMutation: nextSiblingAfterMutation,
                PreviousSiblingAfterMutation: previousSiblingAfterMutation,
                LastMutatedItem: mutatedNames.LastOrDefault(),
                DestroyedTextObservation: destroyedTextObservation,
                IsClosedContentWindowUnresolvable: isClosedContentWindowUnresolvable);
        }
        finally
        {
            StopProvider(provider);
            WaitForDiagnosticOutput(errorOutputTask);
        }
    }

    private static AXUIElement WaitForFocusedNativeElement(
        AXUIElement application,
        string? expectedTitle = null,
        string? expectedValuePrefix = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3.0);
        do
        {
            var element = GetAttributeAsElement(application, AXAttributeConstants.FocusedUIElement, out var error);
            if (element is not null)
            {
                var hasExpectedTitle = expectedTitle is null ||
                    CopyStringAttributeForContent(element, AXAttributeConstants.Title, out var title) == AXError.Success && title == expectedTitle;
                var hasExpectedValue = expectedValuePrefix is null ||
                    CopyStringAttributeRaw(element, AXAttributeConstants.Value, out var value) == AXError.Success &&
                    value?.StartsWith(expectedValuePrefix, StringComparison.Ordinal) == true;
                if (hasExpectedTitle && hasExpectedValue)
                {
                    return element;
                }

                element.Dispose();
            }
            else if (error is AXError.CannotComplete or AXError.APIDisabled or AXError.InvalidUIElement)
            {
                throw new AXException(error, $"Failed to copy the focused content-probe element. AX returned {error}.");
            }

            Thread.Sleep(25);
        } while (Stopwatch.GetTimestamp() < deadline);

        throw new InvalidOperationException("The provider did not expose the expected focused native element.");
    }

    private static List<string> EnumerateChildNames(VisualElement parent, VisualElementQueryRequest request, int maximumCount)
    {
        var names = new List<string>();
        using var children = parent.CreateEnumerator(VisualElementRelation.Child, request);
        while (children.MoveNext())
        {
            if (names.Count >= maximumCount)
            {
                throw new InvalidOperationException($"The child Enumerator exceeded its {maximumCount}-item probe limit.");
            }

            names.Add(children.Current.Snapshot.Name ?? string.Empty);
        }

        return names;
    }

    private static string? ReadFirstRelatedName(VisualElement element, VisualElementRelation relation, VisualElementQueryRequest request)
    {
        using var related = element.CreateEnumerator(relation, request);
        return related.MoveNext() ? related.Current.Snapshot.Name : null;
    }

    private static AXError CopyIntegerAttribute(AXUIElement element, NSString attribute, out long result)
    {
        result = 0;
        var error = AXUIElementCopyAttributeValue(element.Handle.Handle, attribute.Handle.Handle, out var value);
        try
        {
            if (error != AXError.Success || value == 0)
            {
                return error == AXError.Success ? AXError.NoValue : error;
            }

            return CFGetTypeID(value) == CFNumberGetTypeID() &&
                CFNumberGetValue(value, ContentCFNumberType.SInt64, out result) != 0 ?
                AXError.Success :
                AXError.Failure;
        }
        finally
        {
            ReleaseIfNotNull(value);
        }
    }

    private static AXError CopyStringAttributeRaw(AXUIElement element, NSString attribute, out string? result)
    {
        result = null;
        var error = AXUIElementCopyAttributeValue(element.Handle.Handle, attribute.Handle.Handle, out var value);
        try
        {
            if (error != AXError.Success || value == 0)
            {
                return error == AXError.Success ? AXError.NoValue : error;
            }

            result = ReadCoreFoundationString(value);
            return result is null ? AXError.Failure : AXError.Success;
        }
        finally
        {
            ReleaseIfNotNull(value);
        }
    }

    private static unsafe AXError CopyParameterizedStringAttributeRaw(
        AXUIElement element,
        NSString attribute,
        nint location,
        nint length,
        out string? result)
    {
        result = null;
        var range = new NativeCFRange(location, length);
        var parameter = AXValueCreateForContent(AXValueType.CFRange, (nint)(&range));
        if (parameter == 0)
        {
            return AXError.Failure;
        }

        try
        {
            var error = AXUIElementCopyParameterizedAttributeValueForContent(
                element.Handle.Handle,
                attribute.Handle.Handle,
                parameter,
                out var value);
            try
            {
                if (error != AXError.Success || value == 0)
                {
                    return error == AXError.Success ? AXError.NoValue : error;
                }

                result = ReadCoreFoundationString(value);
                return result is null ? AXError.Failure : AXError.Success;
            }
            finally
            {
                ReleaseIfNotNull(value);
            }
        }
        finally
        {
            CFRelease(parameter);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetAttributeValueCount")]
    private static extern AXError GetAttributeValueCountForContent(AXUIElement element, NSString attributeName, out nint count);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CopyAttributeValues")]
    private static extern AXError CopyAttributeValuesForContent(
        AXUIElement element,
        NSString attributeName,
        nint index,
        nint maximumValues,
        out NSArray? values);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CopyStringAttribute")]
    private static extern AXError CopyStringAttributeForContent(AXUIElement element, NSString attributeName, out string? result);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetNativeWindowHandle")]
    private static extern AXError GetNativeWindowHandleForContent(AXUIElement element, out uint nativeWindowHandle);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern AXUIElement CreateAXElementForContent(NativeHandle handle);

    private static AXUIElement CreateAXElementFromArray(NSArray array, nuint index)
    {
        var value = array.ValueAt(index).Handle;
        if (value == 0)
        {
            throw new InvalidOperationException($"The AX array slot {index} was null.");
        }

        return CreateAXElementForContent((NativeHandle)CFRetainForContent(value));
    }

    [LibraryImport(ApplicationServices, EntryPoint = "AXUIElementCopyParameterizedAttributeValue")]
    private static partial AXError AXUIElementCopyParameterizedAttributeValueForContent(
        nint element,
        nint parameterizedAttribute,
        nint parameter,
        out nint value);

    [LibraryImport(ApplicationServices, EntryPoint = "AXValueCreate")]
    private static partial nint AXValueCreateForContent(AXValueType valueType, nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial byte CFNumberGetValue(nint number, ContentCFNumberType numberType, out long value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRetain")]
    private static partial nint CFRetainForContent(nint value);

    private enum ContentCFNumberType
    {
        SInt64 = 4,
    }

    private sealed record ContentBoundariesObservation(
        int ProviderProcessId,
        uint ContentWindowId,
        long NativeTextLength,
        int CompleteValueLength,
        int RangedTextLength,
        AXError RangedTextError,
        int ProductionTextLength,
        int FirstTextPageLength,
        int NonzeroTextPageOffset,
        int NonzeroTextPageLength,
        int SurrogateTextPageLength,
        int FinalTextPageLength,
        AXError FallbackCountError,
        int FallbackTextPageLength,
        int TextPageCount,
        double TextPagingElapsedMilliseconds,
        int NativeChildCount,
        int NativeFirstPageCount,
        int InitialEnumeratedChildCount,
        int MutatedEnumeratedChildCount,
        bool DidMutationPreserveBufferedItem70,
        string? InitialNextSibling,
        string? NextSiblingAfterMutation,
        string? PreviousSiblingAfterMutation,
        string? LastMutatedItem,
        DestroyedElementObservation DestroyedTextObservation,
        bool IsClosedContentWindowUnresolvable);
}
