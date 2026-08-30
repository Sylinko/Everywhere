using Everywhere.Automation;
using Everywhere.Prompting;

namespace Everywhere.Chat;

partial class VisualContextBuilder
{
    /// <summary>
    /// Traversal distance metrics for prioritization.
    /// Global: distance from core elements, Local: distance from the originating node.
    /// </summary>
    /// <param name="Global"></param>
    /// <param name="Local"></param>
    private readonly record struct TraverseDistance(int Global, int Local)
    {
        public static implicit operator TraverseDistance(int distance) => new(distance, distance);

        /// <summary>
        /// Resets the local distance to 1 and increments the global distance by 1.
        /// </summary>
        /// <returns></returns>
        public TraverseDistance Reset() => new(Global + 1, 1);

        /// <summary>
        /// Increments both global and local distances by 1.
        /// </summary>
        /// <returns></returns>
        public TraverseDistance Step() => new(Global + 1, Local + 1);
    }

    /// <summary>
    /// Represents a node in the traversal queue with a calculated priority score.
    /// </summary>
    private readonly record struct TraversalNode(
        VisualElementQueryResult QueryResult,
        VisualElementQueryResult? Previous,
        TraverseDistance Distance,
        VisualContextTraverseDirections Direction,
        int SiblingIndex,
        IVisualElementEnumerator Enumerator,
        string? DirectParentId,
        string? PendingParentAnchorId
    )
    {
        public VisualElement Element => QueryResult.Element;

        public VisualElementSnapshot Snapshot => QueryResult.Snapshot;

        public VisualElementType Type => Snapshot.Type ?? VisualElementType.Unknown;

        /// <summary>
        /// Calculates the final priority score for the Best-First Search algorithm.
        /// Lower value means higher priority (Min-Heap).
        /// <para>
        /// The scoring formula is a multi-dimensional weighted product:
        /// <br/>
        /// <c>FinalScore = -(TopologyScore * IntrinsicScore)</c>
        /// </para>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>1. Topology Score (Distance Decay):</b>
        /// Represents the relevance of the element based on its position in the tree relative to the Core Element.
        /// <br/>
        /// <c>Score_topo = BaseScore / (Distance + 1)</c>
        /// <br/>
        /// - Spine nodes (Ancestors) get a 2x boost.
        /// - Non-spine nodes decay linearly with distance.
        /// </para>
        /// <para>
        /// <b>2. Intrinsic Score (Type Weight):</b>
        /// Represents the inherent importance of the element type.
        /// <br/>
        /// - Interactive controls (Button, Input): 1.5x
        /// - Semantic text (Label): 1.2x
        /// - Containers: 1.0x
        /// - Decorative: 0.5x
        /// </para>
        /// <para>
        /// <b>3. Intrinsic Score (Size Weight):</b>
        /// Represents the visual prominence of the element.
        /// <br/>
        /// <c>Score_size = 1.0 + (Area / ScreenArea)</c>
        /// <br/>
        /// Larger elements are considered more important context.
        /// </para>
        /// <para>
        /// <b>4. Noise Penalty:</b>
        /// Tiny elements (&lt; 5px) receive a 0.1x penalty to filter out visual noise.
        /// </para>
        /// </remarks>
        public float GetScore()
        {
            // Core elements have the highest priority
            if (Direction == VisualContextTraverseDirections.Core) return float.NegativeInfinity;

            // 1. Base score based on topology
            var score = Direction switch
            {
                VisualContextTraverseDirections.Parent => 2000.0f,
                VisualContextTraverseDirections.PreviousSibling => 10000f,
                VisualContextTraverseDirections.NextSibling => 10000f,
                VisualContextTraverseDirections.Child => 1000.0f,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (Distance.Local > 0) score /= Distance.Local; // Linear decay with local distance
            score -= Distance.Global - Distance.Local;

            // We only calculate element properties when direction is Parent or Child
            // because when enumerating siblings, a small weighted element will "block" subsequent siblings.
            var weightedElement = Direction switch
            {
                VisualContextTraverseDirections.Parent => QueryResult,
                VisualContextTraverseDirections.Child => Previous,
                _ => null
            };
            if (weightedElement is not null)
            {
                // 2. Intrinsic Score (Type Weight)
                score *= GetTypeWeight(weightedElement.Snapshot.Type ?? VisualElementType.Unknown);

                // Sometimes the visual element's BoundingRectangle is invalid,
                // but it actually has a valid size that can be obtained from its children.
                // So the following algorithm is not used for now, but we may consider adding it back in the future with some safeguards
                // (e.g., only apply size weight to Panels and TopLevels, and cap the maximum size weight) to prevent potential abuse from noisy bounding rectangles.

                // // 3. Intrinsic Score (Size Weight)
                // // Logarithmic scale for area: log(Area + 1)
                // // Larger elements are usually more important containers or focal points.
                // var rect = weightedElement.BoundingRectangle;
                // if (rect is { Width: > 0, Height: > 0 })
                // {
                //     var area = (float)rect.Width * rect.Height;
                //     // Normalize against a reference screen size (e.g., 1920x1080)
                //     const float screenArea = 1920f * 1080;
                //     var sizeFactor = 1.0f + (area / screenArea);
                //     score *= sizeFactor;
                // }
                //
                // // 4. Penalty for tiny elements (likely noise or invisible)
                // if (rect.Width is > 0 and < 5 || rect.Height is > 0 and < 5)
                // {
                //     score *= 0.1f;
                // }
            }

            // PriorityQueue is a min-heap, so we return negative score to make high scores come first.
            return -score;
        }

        private static float GetTypeWeight(VisualElementType type)
        {
            return type switch
            {
                // Semantic text: High value
                VisualElementType.Label or
                    VisualElementType.TextEdit or
                    VisualElementType.Document => 2.0f,

                // Structural containers: High value
                VisualElementType.Panel or
                    VisualElementType.TopLevel or
                    VisualElementType.TabControl => 1.5f,

                // Interactive controls: Medium value
                VisualElementType.Button or
                    VisualElementType.ComboBox or
                    VisualElementType.CheckBox or
                    VisualElementType.RadioButton or
                    VisualElementType.Slider or
                    VisualElementType.MenuItem or
                    VisualElementType.TabItem => 1.0f,

                // Decorative/Less important: Low value
                VisualElementType.Image or
                    VisualElementType.ScrollBar => 0.5f,

                _ => 1.0f
            };
        }
    }

    private void TryEnqueueTraversalNode(
        PriorityQueue<TraversalNode, float> priorityQueue,
        in TraversalNode? previous,
        in TraverseDistance distance,
        VisualContextTraverseDirections direction,
        IVisualElementEnumerator enumerator)
    {
        if (!enumerator.MoveNext())
        {
            enumerator.Dispose();
            return;
        }

        var queryResult = enumerator.Current;
        retention.Retain(queryResult.Element);
        var directParentId = direction switch
        {
            VisualContextTraverseDirections.Child => previous?.Element.Id,
            VisualContextTraverseDirections.PreviousSibling or VisualContextTraverseDirections.NextSibling => previous?.DirectParentId,
            _ => null
        };
        var pendingParentAnchorId = direction switch
        {
            VisualContextTraverseDirections.Core or VisualContextTraverseDirections.Parent => queryResult.Element.Id,
            VisualContextTraverseDirections.PreviousSibling or VisualContextTraverseDirections.NextSibling when directParentId is null => previous?.PendingParentAnchorId ?? previous?.Element.Id,
            _ => null
        };
        var node = new TraversalNode(
            queryResult,
            previous?.QueryResult,
            distance,
            direction,
            direction switch
            {
                VisualContextTraverseDirections.PreviousSibling => previous?.SiblingIndex - 1 ?? 0,
                VisualContextTraverseDirections.NextSibling => previous?.SiblingIndex + 1 ?? 0,
                _ => enumerator.Index
            },
            enumerator,
            directParentId,
            pendingParentAnchorId);
        var score = node.GetScore();
        priorityQueue.Enqueue(node, score);

#if DEBUG_VISUAL_TREE_BUILDER
        _debugRecorder?.RegisterNode(node.QueryResult, node.GetScore());
        _debugRecorder?.RecordStep(
            node.QueryResult,
            "Enqueue",
            score,
            $"Parent: {node.DirectParentId}, Previous: {node.Previous?.Element.Id}, Direction: {node.Direction}, Distance: {node.Distance}",
            0,
            priorityQueue.Count);
#endif
    }

    private void ProcessTraversalQueue(
        PriorityQueue<TraversalNode, float> priorityQueue,
        Dictionary<string, VisualElementNode> visitedElements,
        CancellationToken cancellationToken)
    {
        var accumulatedTokenCount = 0;
        var resolvedParentAnchors = new Dictionary<string, string>(StringComparer.Ordinal);

        while (priorityQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remainingTokenCount = approximateTokenLimit - accumulatedTokenCount;
            if (remainingTokenCount <= 0)
            {
#if DEBUG_VISUAL_TREE_BUILDER
                _debugRecorder?.RecordStep(
                    priorityQueue.Peek().QueryResult,
                    "Stop",
                    0,
                    "Token limit reached",
                    accumulatedTokenCount,
                    priorityQueue.Count);
#endif
                break;
            }

#if DEBUG_VISUAL_TREE_BUILDER
            if (!priorityQueue.TryDequeue(out var node, out var priority)) break;
#else
            if (!priorityQueue.TryDequeue(out var node, out _)) break;
#endif
            var element = node.Element;
            var id = element.Id;

            effectScope?.Add(node.QueryResult);

            if (visitedElements.ContainsKey(id))
            {
#if DEBUG_VISUAL_TREE_BUILDER
                _debugRecorder?.RecordStep(node.QueryResult, "Skip", priority, "Already visited", accumulatedTokenCount, priorityQueue.Count);
#endif
                node.Enumerator.Dispose();
                continue;
            }

            // Process the current node and create the VisualElementNode
            CreateVisualElementNode(visitedElements, resolvedParentAnchors, node, remainingTokenCount, ref accumulatedTokenCount);

#if DEBUG_VISUAL_TREE_BUILDER
            _debugRecorder?.RecordStep(
                node.QueryResult,
                "Visit",
                priority,
                $"Parent: {node.DirectParentId}, Previous: {node.Previous?.Element.Id}, Direction: {node.Direction}, Distance: {node.Distance}",
                accumulatedTokenCount,
                priorityQueue.Count);
#endif

            // Check limit again after adding this node
            if (accumulatedTokenCount > approximateTokenLimit) break;

            // Add more nodes to the queue based on traversal direction
            PropagateNode(priorityQueue, node);
        }
    }

    private void CreateVisualElementNode(
        Dictionary<string, VisualElementNode> visitedElements,
        Dictionary<string, string> resolvedParentAnchors,
        TraversalNode traversalNode,
        int remainingTokenCount,
        ref int accumulatedTokenCount)
    {
        var element = traversalNode.Element;
        var id = element.Id;
        var snapshot = traversalNode.Snapshot;
        var type = traversalNode.Type;

        // --- Determine Content and Self-Informativeness ---
        string? description = null;
        string? content = null;
        var isContentOmitted = false;
        var isTextElement = type is VisualElementType.Label or VisualElementType.TextEdit or VisualElementType.Document;
        var text = snapshot.TextPreview;
        if (snapshot.Name is { Length: > 0 } name)
        {
            if (isTextElement && string.IsNullOrEmpty(text))
            {
                content = OmitIfNeeded(name, Math.Max(remainingTokenCount, 50), out var omittedLength);
                remainingTokenCount -= omittedLength;
                isContentOmitted |= omittedLength > 0;
            }
            else if (!isTextElement || !ApproximatelyEquals(name, text))
            {
                description = OmitIfNeeded(name, Math.Max(remainingTokenCount, 50), out var omittedLength);
                remainingTokenCount -= omittedLength;
                isContentOmitted |= omittedLength > 0;
            }
        }
        if (content is null && text is { Length: > 0 })
        {
            content = OmitIfNeeded(text, Math.Max(remainingTokenCount, 50), out var omittedLength);
            isContentOmitted |= omittedLength > 0;
        }

        var contentLines = content?.Split(Environment.NewLine) ?? [];
        var hasTextContent = contentLines.Length > 0;
        var hasDescription = !string.IsNullOrWhiteSpace(description);
        var interactive = IsInteractiveElement(type, snapshot.States ?? VisualElementStates.None);
        var isCoreElement = _coreElementIdSet.Contains(id);
        var isSelfInformative = hasTextContent || hasDescription || interactive || isCoreElement;

        // --- Calculate Token Costs ---
        // Cost varies by output format: XML is verbose, JSON is compact, TOON is tabular (header amortized).
        var selfTokenCount = detailLevel switch
        {
            VisualContextDetailLevel.Detailed => 8, // XML: <Type id="N">...</Type>
            VisualContextDetailLevel.Compact => 5, // JSON: {"t":"Type","id":N,...}
            _ => 2 // TOON: row values only (header amortized)
        };

        // Add cost for bounds attributes if applicable (x, y, width, height)
        if (ShouldIncludeBounds(detailLevel, type))
        {
            selfTokenCount += detailLevel switch
            {
                VisualContextDetailLevel.Detailed => 20, // pos="x,y" size="wxh"
                VisualContextDetailLevel.Compact => 10, // ,"pos":"x,y","size":"wxh"
                _ => 4 // ,x,y,wxh
            };
        }

        var attrOverhead = detailLevel switch
        {
            VisualContextDetailLevel.Detailed => 3, // description="..."
            VisualContextDetailLevel.Compact => 2, // ,"desc":"..."
            _ => 1 // ,value
        };
        var lineOverhead = detailLevel == VisualContextDetailLevel.Detailed ? 4 : 0; // XML indentation per line; JSON/TOON join with \n
        var blockOverhead = detailLevel switch
        {
            VisualContextDetailLevel.Detailed => 8, // end tag
            VisualContextDetailLevel.Compact => 2, // ,"content":"..."
            _ => 1 // ,value
        };

        var contentTokenCount = 0;
        if (description != null) contentTokenCount += TokenHelper.EstimateTokenCount(description) + attrOverhead;
        contentTokenCount += contentLines.Length switch
        {
            > 0 and < 3 => contentLines.Sum(TokenHelper.EstimateTokenCount),
            >= 3 => contentLines.Sum(line => TokenHelper.EstimateTokenCount(line) + lineOverhead) + blockOverhead,
            _ => 0
        };

        // Create the XML Element node
        var elementNode = visitedElements[id] = new VisualElementNode(
            traversalNode.QueryResult,
            traversalNode.PendingParentAnchorId,
            traversalNode.SiblingIndex,
            description,
            contentLines,
            selfTokenCount,
            contentTokenCount,
            isSelfInformative,
            traversalNode.Direction == VisualContextTraverseDirections.Core)
        {
            IsContentOmitted = isContentOmitted
        };

        // --- Update Token Count and Propagate ---

        // If the element is self-informative, it is active immediately.
        if (elementNode.IsVisible || type is not VisualElementType.TopLevel and not VisualElementType.Screen)
        {
            accumulatedTokenCount += elementNode.TokenCount + elementNode.ContentTokenCount;
        }

        // Link to parent and propagate updates
        var directParentId = traversalNode.DirectParentId;
        if (directParentId is null && traversalNode.PendingParentAnchorId is { } pendingAnchorId)
        {
            resolvedParentAnchors.TryGetValue(pendingAnchorId, out directParentId);
        }

        if (directParentId is not null && visitedElements.TryGetValue(directParentId, out var parentXmlElement))
        {
            AttachChild(parentXmlElement, elementNode, ref accumulatedTokenCount);
        }
        if (traversalNode is { Direction: VisualContextTraverseDirections.Parent, Previous: { } previous })
        {
            var anchorId = previous.Element.Id;
            if (visitedElements.TryGetValue(anchorId, out var anchorNode))
            {
                anchorId = anchorNode.PendingParentAnchorId ?? anchorId;
            }

            resolvedParentAnchors[anchorId] = id;
            foreach (var childElementNode in visitedElements.Values.AsValueEnumerable().Where(candidate => candidate.Parent is null && string.Equals(candidate.PendingParentAnchorId, anchorId, StringComparison.Ordinal)).ToArray())
            {
                childElementNode.PendingParentAnchorId = null;
                AttachChild(elementNode, childElementNode, ref accumulatedTokenCount);
            }
        }

        return;

        void AttachChild(VisualElementNode parentNode, VisualElementNode childNode, ref int tokenCount)
        {
            if (!parentNode.Children.Add(childNode))
            {
                return;
            }

            childNode.Parent = parentNode;
            if (childNode.IsSelfInformative || childNode.HasInformativeDescendants)
            {
                PropagateInformativeUpdate(parentNode, ref tokenCount);
            }
        }
    }

    /// <summary>
    /// Propagates the information that a child is informative up the tree.
    /// This may cause ancestors to become active (rendered) if they meet the criteria for the current <see cref="detailLevel"/>.
    /// </summary>
    private void PropagateInformativeUpdate(VisualElementNode? parent, ref int accumulatedTokenCount)
    {
        while (parent != null)
        {
            parent.InformativeChildCount++;

            var wasActive = parent.IsVisible;
            var wasHasInfo = parent.HasInformativeDescendants;

            parent.HasInformativeDescendants = true;

            // Check if activation state changes based on the new child count
            UpdateActivationState(parent);

            if (!wasActive && parent.IsVisible)
            {
                // Parent just became active, so we must pay for its structure tokens.
                accumulatedTokenCount += parent.TokenCount;
                // Note: ContentTokenCount is 0 for non-self-informative elements, so we don't add it.
            }

            // If the parent already had informative descendants, we don't need to propagate the "existence" of info further up.
            // The ancestors already know this branch is informative.
            // However, we DO need to continue if the parent's activation state changed, because that might affect token count?
            // No, token count is updated locally.
            // Does parent activation affect grandparent activation?
            // Grandparent activation depends on grandparent.InformativeChildCount.
            // Grandparent.InformativeChildCount counts children that are "informative" (HasInformativeContent).
            // HasInformativeContent = IsSelfInformative || HasInformativeDescendants.
            // Since parent.HasInformativeDescendants was already true (if wasHasInfo is true),
            // parent was already contributing to grandparent's InformativeChildCount.
            // So grandparent's count doesn't change.

            if (wasHasInfo) break;

            parent = parent.Parent;
        }
    }

    /// <summary>
    /// Updates the <see cref="VisualElementNode.IsVisible"/> state of an element based on the current <see cref="detailLevel"/>
    /// and its informative status.
    /// </summary>
    private void UpdateActivationState(VisualElementNode element)
    {
        // If it's self-informative, it's always active.
        if (element.IsSelfInformative)
        {
            element.IsVisible = true;
            return;
        }

        // Otherwise, it depends on the detail level and children.
        var shouldRender = detailLevel switch
        {
            VisualContextDetailLevel.Compact => ShouldKeepContainerForCompact(element),
            VisualContextDetailLevel.Minimal => ShouldKeepContainerForMinimal(element),
            // For Detailed, we render if there are any informative descendants.
            _ => element.HasInformativeDescendants
        };

        element.IsVisible = shouldRender;
    }

    private static bool ShouldKeepContainerForCompact(VisualElementNode element)
    {
        if (element.Parent is null) return element.InformativeChildCount > 0;

        return element.Type switch
        {
            VisualElementType.Screen or VisualElementType.TopLevel => true,
            VisualElementType.Document => element.InformativeChildCount > 0,
            VisualElementType.Panel => element.InformativeChildCount > 1,
            _ => false
        };
    }

    private static bool ShouldKeepContainerForMinimal(VisualElementNode element)
    {
        return element.Parent is null && element.InformativeChildCount > 0;
    }

#if DEBUG_VISUAL_TREE_BUILDER
    private void PropagateNode(
#else
    private void PropagateNode(
#endif
        PriorityQueue<TraversalNode, float> priorityQueue,
        in TraversalNode node)
    {
#if DEBUG_VISUAL_TREE_BUILDER
        Debug.WriteLine($"[PropagateNode] {node}");
#endif

        var elementType = node.Type;
        switch (node.Direction)
        {
            case VisualContextTraverseDirections.Core:
            {
                // In this case, node.Enumerator is the core element enumerator
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    0,
                    VisualContextTraverseDirections.Core,
                    node.Enumerator);

                // Only enqueue parent and siblings if not top-level
                if (elementType != VisualElementType.TopLevel)
                {
                    if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.Parent))
                        TryEnqueueTraversalNode(
                            priorityQueue,
                            node,
                            1,
                            VisualContextTraverseDirections.Parent,
                            node.Element.CreateEnumerator(VisualElementRelation.Parent, new VisualElementEnumerationOptions(_queryRequest)));

                    if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.PreviousSibling))
                        TryEnqueueTraversalNode(
                            priorityQueue,
                            node,
                            1,
                            VisualContextTraverseDirections.PreviousSibling,
                            node.Element.CreateEnumerator(VisualElementRelation.PreviousSibling, new VisualElementEnumerationOptions(_queryRequest)));

                    if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.NextSibling))
                        TryEnqueueTraversalNode(
                            priorityQueue,
                            node,
                            1,
                            VisualContextTraverseDirections.NextSibling,
                            node.Element.CreateEnumerator(VisualElementRelation.NextSibling, new VisualElementEnumerationOptions(_queryRequest)));
                }

                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.Child))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        1,
                        VisualContextTraverseDirections.Child,
                        node.Element.CreateEnumerator(VisualElementRelation.Child, new VisualElementEnumerationOptions(_queryRequest)));
                break;
            }
            case VisualContextTraverseDirections.Parent when elementType != VisualElementType.TopLevel:
            {
                node.Enumerator.Dispose();
                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.Parent))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualContextTraverseDirections.Parent,
                        node.Element.CreateEnumerator(VisualElementRelation.Parent, new VisualElementEnumerationOptions(_queryRequest)));

                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.PreviousSibling))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualContextTraverseDirections.PreviousSibling,
                        node.Element.CreateEnumerator(VisualElementRelation.PreviousSibling, new VisualElementEnumerationOptions(_queryRequest)));

                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.NextSibling))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualContextTraverseDirections.NextSibling,
                        node.Element.CreateEnumerator(VisualElementRelation.NextSibling, new VisualElementEnumerationOptions(_queryRequest)));
                break;
            }
            case VisualContextTraverseDirections.Parent:
            {
                node.Enumerator.Dispose();
                break;
            }
            case VisualContextTraverseDirections.PreviousSibling:
            {
                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.PreviousSibling))
                    // In this case, node.Enumerator is the Previous Sibling enumerator
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualContextTraverseDirections.PreviousSibling,
                        node.Enumerator);

                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.Child))
                    // Also enqueue the children of this sibling
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualContextTraverseDirections.Child,
                        node.Element.CreateEnumerator(VisualElementRelation.Child, new VisualElementEnumerationOptions(_queryRequest)));
                break;
            }
            case VisualContextTraverseDirections.NextSibling:
            {
                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.NextSibling))
                    // In this case, node.Enumerator is the Next Sibling enumerator
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualContextTraverseDirections.NextSibling,
                        node.Enumerator);

                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.Child))
                    // Also enqueue the children of this sibling
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualContextTraverseDirections.Child,
                        node.Element.CreateEnumerator(VisualElementRelation.Child, new VisualElementEnumerationOptions(_queryRequest)));
                break;
            }
            case VisualContextTraverseDirections.Child:
            {
                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.NextSibling))
                    // In this case, node.Enumerator is the Children enumerator
                    // But note that these children are actually descendants of the original node's sibling.
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualContextTraverseDirections.NextSibling,
                        node.Enumerator);

                if (allowedTraverseDirections.HasFlag(VisualContextTraverseDirections.Child))
                    // Also enqueue the children of this child
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualContextTraverseDirections.Child,
                        node.Element.CreateEnumerator(VisualElementRelation.Child, new VisualElementEnumerationOptions(_queryRequest)));
                break;
            }
        }
    }

    private sealed class CoreElementEnumerator(IReadOnlyList<VisualElement> elements, VisualElementQueryRequest queryRequest) : IVisualElementEnumerator
    {
        public VisualElementQueryResult Current => _current ?? throw new InvalidOperationException("The Enumerator has no current item.");

        object IEnumerator.Current => Current;

        public int Count => elements.Count;

        public int Index { get; private set; } = -1;

        public bool HasMore => Index + 1 < elements.Count;

        private VisualElementQueryResult? _current;
        private bool _isDisposed;

        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var nextIndex = Index + 1;
            if (nextIndex >= elements.Count)
            {
                Index = -1;
                _current = null;
                return false;
            }

            Index = nextIndex;
            _current = elements[nextIndex].Query(queryRequest);
            return true;
        }

        public void Reset() => throw new NotSupportedException("The core-element Enumerator cannot be reset.");

        public void Dispose()
        {
            _isDisposed = true;
            _current = null;
        }
    }
}
