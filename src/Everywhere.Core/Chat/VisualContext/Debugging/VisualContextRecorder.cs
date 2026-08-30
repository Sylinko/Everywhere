#if DEBUG

using Everywhere.Automation;
using ZLinq;

namespace Everywhere.Chat;

public class VisualContextRecorder(
    IReadOnlyList<VisualElement> coreElements,
    int tokenLimit,
    string algorithmName)
{
    private readonly List<DebugVisualNode> _allNodes = [];
    private readonly List<DebugTraversalStep> _steps = [];
    private readonly HashSet<string> _knownIds = [];
    private int _stepCounter;
    private int _accumulatedTokenCount;

    public void RegisterNode(VisualElementQueryResult queryResult, float score)
    {
        var element = queryResult.Element;
        if (!_knownIds.Add(element.Id)) return;

        var snapshot = queryResult.Snapshot;
        var rect = snapshot.Bounds.GetValueOrDefault();
        _allNodes.Add(new DebugVisualNode(
            score,
            element.Id,
            (snapshot.Type ?? VisualElementType.Unknown).ToString(),
            snapshot.Name,
            snapshot.TextPreview,
            [rect.X, rect.Y, rect.Width, rect.Height],
            [],
            coreElements.AsValueEnumerable().Any(c => c.Id == element.Id)
        ));
    }

    public void RecordStep(VisualElementQueryResult queryResult, string action, double score, string reason, int accumulatedTokenCount, int queueSize)
    {
        _steps.Add(new DebugTraversalStep(
            _stepCounter++,
            queryResult.Element.Id,
            action,
            score,
            reason,
            _accumulatedTokenCount = Math.Max(_accumulatedTokenCount, accumulatedTokenCount),
            queueSize
        ));
    }

    public void SaveSession(string filePath)
    {
        var session = new DebugSession(
            [.. _allNodes],
            [.. _steps],
            algorithmName,
            tokenLimit
        );
        File.WriteAllText(filePath, session.ToJson());
    }
}

#endif
