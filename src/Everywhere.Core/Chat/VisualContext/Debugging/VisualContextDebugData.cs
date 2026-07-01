using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.Chat;

public record DebugVisualNode(
    float Score,
    string Id,
    string Type,
    string? Name,
    string? Text,
    int[] Rect, // [x, y, w, h]
    IList<string> ChildrenIds,
    bool IsCore
);

public record DebugTraversalStep(
    int StepIndex,
    string NodeId,
    string Action, // "Enqueue", "Visit", "Skip", "Stop"
    double Score,
    string Reason,
    int CurrentTokens,
    int QueueSize
);

public record DebugSession(
    IList<DebugVisualNode> AllNodes,
    IList<DebugTraversalStep> Steps,
    string AlgorithmName,
    int TokenLimit
)
{
    public string ToJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        return JsonSerializer.Serialize(this, options);
    }
}
