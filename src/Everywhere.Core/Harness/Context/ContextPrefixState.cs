using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;
using Everywhere.Chat.Plugins;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// Identifies the immutable structural bounds of one prefix-caching epoch.
/// </summary>
public readonly record struct ContextPrefixIdentity(
    string Dialect,
    string? ModelId,
    bool SupportsToolCall,
    Guid? ActiveSummaryNodeId,
    Guid? WindowStartNodeId
);

/// <summary>
/// Represents a keyed, non-persistent runtime reminder injected through <see cref="ContextPrefixState"/>.
/// </summary>
/// <param name="Key">Stable identifier used to deduplicate unchanged notices within an epoch.</param>
/// <param name="Message">Reminder text emitted when added or modified.</param>
/// <param name="ClearedMessage">Optional notice emitted once if this key disappears later in the same epoch.</param>
public readonly record struct ContextTransientReminder(
    string Key,
    string Message,
    string? ClearedMessage = null
);

/// <summary>
/// Represents the effective wire history and tool catalog produced for one conversational request.
/// </summary>
public sealed record ContextPrefixResolution(
    IList<ChatMessage> History,
    IList<AITool>? Tools,
    bool HasResetEpoch
);

/// <summary>
/// Preserves prompt-cache prefixes in memory across consecutive turns and tool calls of
/// an active chat session without altering persisted message storage.
/// </summary>
/// <remarks>
/// Provider KV caches match strictly on leading <c>(tools, system, messages)</c> bytes within a
/// short activity TTL. Whenever non-breaking changes occur mid-epoch (system instruction updates
/// or disabled tools), this state keeps the initial prefix byte-identical and appends a concise
/// <c>&lt;system-reminder&gt;</c> user message anchored deterministically in the message tail.
/// </remarks>
public sealed class ContextPrefixState
{
    private const int DiffContextLines = 1;

    /// <summary>
    /// Default inactivity window matching typical provider ephemeral KV-cache lifetimes.
    /// </summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _cacheTtl;
    private readonly Lock _syncLock = new();
    private EpochSnapshot? _snapshot;

    /// <summary>
    /// Initializes a new <see cref="ContextPrefixState"/> with the default provider cache TTL.
    /// </summary>
    public ContextPrefixState() : this(DefaultCacheTtl)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ContextPrefixState"/> with the supplied cache TTL.
    /// </summary>
    public ContextPrefixState(TimeSpan cacheTtl)
    {
        if (cacheTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheTtl), "Cache TTL must be positive.");
        }

        _cacheTtl = cacheTtl;
    }

    /// <summary>
    /// Clears any active prefix epoch snapshot.
    /// </summary>
    public void Reset()
    {
        using var scope = _syncLock.EnterScope();
        _snapshot = null;
    }

    /// <summary>
    /// Resolves the wire history, tool list, and keyed non-persistent runtime reminders for one
    /// conversational model request from a resolved <see cref="IChatPluginScope"/>.
    /// </summary>
    public ContextPrefixResolution Apply(
        IList<ChatMessage> history,
        IChatPluginScope? currentScope,
        ContextPrefixIdentity identity,
        DateTimeOffset utcNow
    )
    {
        var (hasSystemHead, systemPrompt, capturedMessages, currentHashes) = CaptureHistory(history);
        var activeTools = currentScope?.Declarations is { Count: > 0 } declarations ? declarations : null;
        var currentReminders = NormalizeTransientReminders(currentScope?.TransientReminders);

        using var scope = _syncLock.EnterScope();
        if (_snapshot is null || ShouldResetEpoch(
                _snapshot,
                activeTools,
                currentScope,
                identity,
                hasSystemHead,
                currentHashes,
                utcNow,
                out var commonPrefixLength))
        {
            var lockedTools = activeTools is null ? [] : activeTools.ToArray();
            _snapshot = new EpochSnapshot(
                identity,
                hasSystemHead,
                systemPrompt,
                lockedTools,
                BuildToolFingerprints(lockedTools, currentScope),
                currentHashes,
                utcNow);

            if (currentReminders.Count > 0)
            {
                var lines = currentReminders
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => pair.Value.Message)
                    .ToList();
                var anchorCount = capturedMessages.Length > 0 && capturedMessages[^1].Role == ChatRole.User
                    ? capturedMessages.Length - 1
                    : capturedMessages.Length;
                _snapshot.Reminders.Add(
                    new PrefixReminderEntry(
                        anchorCount,
                        FormatSystemReminder(lines),
                        systemPrompt,
                        EmptyStringSet.Instance,
                        currentReminders));

                var initialEffectiveHistory = BuildEffectiveHistory(
                    hasSystemHead,
                    _snapshot.InitialSystemPrompt,
                    capturedMessages,
                    _snapshot.Reminders,
                    capturedMessages.Length);
                return new ContextPrefixResolution(
                    initialEffectiveHistory,
                    lockedTools.Length > 0 ? [.. lockedTools] : null,
                    HasResetEpoch: true);
            }

            return new ContextPrefixResolution(
                history,
                lockedTools.Length > 0 ? [.. lockedTools] : null,
                HasResetEpoch: true);
        }

        var snapshot = _snapshot;
        // Drop any reminder whose anchor belongs to a pruned or edited branch suffix.
        snapshot.Reminders.RemoveAll(reminder => reminder.AfterMessageCount > commonPrefixLength);

        var (effectiveSystemPrompt, effectiveDisabledTools, effectiveReminders) = snapshot.Reminders.Count > 0
            ? (
                snapshot.Reminders[^1].AppliedSystemPrompt,
                snapshot.Reminders[^1].AppliedDisabledTools,
                snapshot.Reminders[^1].AppliedTransientReminders)
            : (snapshot.InitialSystemPrompt, EmptyStringSet.Instance, EmptyReminderMap.Instance);

        var currentlyDisabledTools = ComputeDisabledToolNames(snapshot.LockedToolNames, activeTools);
        var deltaLines = BuildDeltaLines(
            systemPrompt,
            effectiveSystemPrompt,
            snapshot.InitialSystemPrompt,
            currentlyDisabledTools,
            effectiveDisabledTools,
            currentReminders,
            effectiveReminders);

        if (deltaLines.Count > 0)
        {
            var reminderText = FormatSystemReminder(deltaLines);
            var anchorCount = commonPrefixLength < capturedMessages.Length && capturedMessages[^1].Role == ChatRole.User
                ? capturedMessages.Length - 1
                : capturedMessages.Length;
            snapshot.Reminders.Add(
                new PrefixReminderEntry(anchorCount, reminderText, systemPrompt, currentlyDisabledTools, currentReminders));
        }

        snapshot.LastRequestMessageHashes = currentHashes;
        snapshot.LastUsedAt = utcNow;

        var effectiveHistory = BuildEffectiveHistory(
            hasSystemHead,
            snapshot.InitialSystemPrompt,
            capturedMessages,
            snapshot.Reminders,
            capturedMessages.Length);

        return new ContextPrefixResolution(
            effectiveHistory,
            snapshot.LockedTools.Length > 0 ? [.. snapshot.LockedTools] : null,
            HasResetEpoch: false);
    }

    /// <summary>
    /// Projects the currently active prefix epoch (locked system prompt, locked tools, and surviving
    /// anchored <c>&lt;system-reminder&gt;</c> entries) onto <paramref name="history"/> without mutating
    /// epoch state, returning <see langword="null"/> when no matching active prefix can be reused.
    /// </summary>
    public ContextPrefixResolution? ProjectReadOnly(
        IList<ChatMessage> history,
        ContextPrefixIdentity identity,
        DateTimeOffset utcNow
    )
    {
        var (hasSystemHead, _, capturedMessages, currentHashes) = CaptureHistory(history);

        using var scope = _syncLock.EnterScope();
        if (_snapshot is null)
        {
            return null;
        }

        var snapshot = _snapshot;
        if (!CanReusePrefix(snapshot, identity, hasSystemHead, currentHashes, utcNow, out var commonPrefixLength))
        {
            return null;
        }

        var effectiveHistory = BuildEffectiveHistory(
            hasSystemHead,
            snapshot.InitialSystemPrompt,
            capturedMessages,
            snapshot.Reminders,
            commonPrefixLength);

        return new ContextPrefixResolution(
            effectiveHistory,
            snapshot.LockedTools.Length > 0 ? [.. snapshot.LockedTools] : null,
            HasResetEpoch: false);
    }

    private static (bool HasSystemHead, string SystemPrompt, ChatMessage[] CapturedMessages, MessageHash[] Hashes) CaptureHistory(
        IList<ChatMessage> history
    )
    {
        var hasSystemHead = history.Count > 0 && history[0].Role == ChatRole.System;
        var systemPrompt = hasSystemHead ? history[0].Text : string.Empty;
        var offset = hasSystemHead ? 1 : 0;
        var capturedCount = history.Count - offset;
        var capturedMessages = new ChatMessage[capturedCount];
        var hashes = new MessageHash[capturedCount];
        for (var i = 0; i < capturedCount; i++)
        {
            var message = history[i + offset];
            capturedMessages[i] = message;
            hashes[i] = ComputeMessageHash(message);
        }

        return (hasSystemHead, systemPrompt, capturedMessages, hashes);
    }

    private static List<ChatMessage> BuildEffectiveHistory(
        bool hasSystemHead,
        string initialSystemPrompt,
        ChatMessage[] capturedMessages,
        List<PrefixReminderEntry> reminders,
        int maxReminderAnchor
    )
    {
        var effectiveHistory = new List<ChatMessage>(capturedMessages.Length + reminders.Count + (hasSystemHead ? 1 : 0));
        if (hasSystemHead)
        {
            effectiveHistory.Add(new ChatMessage(ChatRole.System, initialSystemPrompt));
        }

        var reminderCursor = 0;
        for (var i = 0; i <= capturedMessages.Length; i++)
        {
            while (reminderCursor < reminders.Count &&
                   reminders[reminderCursor].AfterMessageCount <= maxReminderAnchor &&
                   reminders[reminderCursor].AfterMessageCount == i)
            {
                effectiveHistory.Add(new ChatMessage(ChatRole.User, reminders[reminderCursor].Content));
                reminderCursor++;
            }

            if (i < capturedMessages.Length)
            {
                effectiveHistory.Add(capturedMessages[i]);
            }
        }

        return effectiveHistory;
    }

    private bool CanReusePrefix(
        EpochSnapshot snapshot,
        ContextPrefixIdentity identity,
        bool hasSystemHead,
        MessageHash[] currentHashes,
        DateTimeOffset utcNow,
        out int commonPrefixLength
    )
    {
        commonPrefixLength = CountCommonPrefix(snapshot.LastRequestMessageHashes, currentHashes);
        if (utcNow - snapshot.LastUsedAt > _cacheTtl) return false;
        if (snapshot.Identity != identity) return false;
        if (snapshot.HasSystemHead != hasSystemHead) return false;
        return currentHashes.Length == 0 || snapshot.LastRequestMessageHashes.Length == 0 || commonPrefixLength > 0;
    }

    private bool ShouldResetEpoch(
        EpochSnapshot snapshot,
        IReadOnlyList<AIFunctionDeclaration>? currentTools,
        IChatPluginScope? currentScope,
        ContextPrefixIdentity identity,
        bool hasSystemHead,
        MessageHash[] currentHashes,
        DateTimeOffset utcNow,
        out int commonPrefixLength
    )
    {
        if (!CanReusePrefix(snapshot, identity, hasSystemHead, currentHashes, utcNow, out commonPrefixLength))
        {
            return true;
        }

        return HasAdditiveOrModifiedTools(currentTools, currentScope, snapshot.ToolFingerprints);
    }

    private static int CountCommonPrefix(MessageHash[] previous, MessageHash[] current)
    {
        var limit = Math.Min(previous.Length, current.Length);
        var count = 0;
        while (count < limit && previous[count] == current[count])
        {
            count++;
        }

        return count;
    }

    private static bool HasAdditiveOrModifiedTools(
        IReadOnlyList<AIFunctionDeclaration>? currentTools,
        IChatPluginScope? currentScope,
        Dictionary<string, ToolFingerprint> lockedFingerprints
    )
    {
        if (currentTools is null || currentTools.Count == 0) return false;

        foreach (var tool in currentTools)
        {
            if (!lockedFingerprints.TryGetValue(tool.Name, out var locked))
            {
                return true;
            }

            var pluginKey = ResolvePluginKey(currentScope, tool.Name);
            if (!string.Equals(pluginKey, locked.PluginKey, StringComparison.Ordinal) ||
                !string.Equals(tool.Description, locked.Description, StringComparison.Ordinal) ||
                !string.Equals(tool.JsonSchema.GetRawText(), locked.SchemaRawText, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, ToolFingerprint> BuildToolFingerprints(
        AIFunctionDeclaration[] tools,
        IChatPluginScope? currentScope
    )
    {
        var map = new Dictionary<string, ToolFingerprint>(tools.Length, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            map[tool.Name] = new ToolFingerprint(
                ResolvePluginKey(currentScope, tool.Name),
                tool.Description,
                tool.JsonSchema.GetRawText());
        }

        return map;
    }

    private static string? ResolvePluginKey(IChatPluginScope? scope, string toolName) =>
        scope is not null && scope.TryGetBinding(toolName, out var binding, out _) ? binding.Plugin.Key : null;

    private static HashSet<string> ComputeDisabledToolNames(
        string[] lockedToolNames,
        IReadOnlyList<AIFunctionDeclaration>? currentTools
    )
    {
        if (lockedToolNames.Length == 0) return EmptyStringSet.Instance;

        if (currentTools is null || currentTools.Count == 0)
        {
            return new HashSet<string>(lockedToolNames, StringComparer.Ordinal);
        }

        var enabled = new HashSet<string>(currentTools.Count, StringComparer.Ordinal);
        foreach (var tool in currentTools)
        {
            enabled.Add(tool.Name);
        }

        HashSet<string>? disabled = null;
        foreach (var name in lockedToolNames)
        {
            if (!enabled.Contains(name))
            {
                (disabled ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
            }
        }

        return disabled ?? EmptyStringSet.Instance;
    }

    private static List<string> BuildDeltaLines(
        string currentSystemPrompt,
        string previousSystemPrompt,
        string initialSystemPrompt,
        HashSet<string> currentDisabledTools,
        HashSet<string> previousDisabledTools,
        Dictionary<string, ContextTransientReminder> currentReminders,
        Dictionary<string, ContextTransientReminder> previousReminders
    )
    {
        var lines = new List<string>();

        var newlyDisabled = currentDisabledTools
            .Where(name => !previousDisabledTools.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (newlyDisabled.Length > 0)
        {
            lines.Add(
                $"The following tools are no longer available and must not be called: {string.Join(", ", newlyDisabled)}.");
        }

        var reenabled = previousDisabledTools
            .Where(name => !currentDisabledTools.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (reenabled.Length > 0)
        {
            lines.Add(
                $"The following previously disabled tools are available again: {string.Join(", ", reenabled)}.");
        }

        if (BuildSystemPromptDelta(currentSystemPrompt, previousSystemPrompt, initialSystemPrompt) is { } promptDelta)
        {
            lines.Add(promptDelta);
        }

        AppendTransientReminderDeltas(lines, currentReminders, previousReminders);

        return lines;
    }

    private static Dictionary<string, ContextTransientReminder> NormalizeTransientReminders(
        IReadOnlyList<ContextTransientReminder>? transientReminders
    )
    {
        if (transientReminders is null || transientReminders.Count == 0)
        {
            return EmptyReminderMap.Instance;
        }

        Dictionary<string, ContextTransientReminder>? result = null;
        foreach (var reminder in transientReminders)
        {
            if (string.IsNullOrWhiteSpace(reminder.Key) || string.IsNullOrWhiteSpace(reminder.Message))
            {
                continue;
            }

            result ??= new Dictionary<string, ContextTransientReminder>(StringComparer.Ordinal);
            result[reminder.Key] = reminder;
        }

        return result ?? EmptyReminderMap.Instance;
    }

    private static void AppendTransientReminderDeltas(
        List<string> lines,
        Dictionary<string, ContextTransientReminder> currentReminders,
        Dictionary<string, ContextTransientReminder> previousReminders
    )
    {
        foreach (var pair in currentReminders.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (!previousReminders.TryGetValue(pair.Key, out var previous) ||
                !string.Equals(previous.Message, pair.Value.Message, StringComparison.Ordinal))
            {
                lines.Add(pair.Value.Message);
            }
        }

        foreach (var pair in previousReminders.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (!currentReminders.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.ClearedMessage))
            {
                lines.Add(pair.Value.ClearedMessage);
            }
        }
    }

    private static string? BuildSystemPromptDelta(
        string current,
        string previous,
        string initial
    )
    {
        if (string.Equals(current, previous, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(current, initial, StringComparison.Ordinal))
        {
            return "Temporary instruction changes have ended. Follow the original system instructions from the beginning of the conversation.";
        }

        var currentNormalized = current.ReplaceLineEndings("\n");
        if (string.IsNullOrWhiteSpace(currentNormalized))
        {
            return "Previous system instructions have been cleared for subsequent turns.";
        }

        var previousNormalized = previous.ReplaceLineEndings("\n");
        var diffResult = Differ.Instance.CreateDiffs(
            previousNormalized,
            currentNormalized,
            ignoreWhiteSpace: false,
            ignoreCase: false,
            LineChunker.Instance);
        if (diffResult.DiffBlocks.Count == 0)
        {
            return null;
        }

        if (!HasSurvivingLines(diffResult))
        {
            return $"Updated system instructions (supersedes earlier conflicting instructions for subsequent turns):\n{current}";
        }

        var unifiedDiff = FormatUnifiedDiff(diffResult);
        var diffMessage =
            $"System instructions updated (unified diff against earlier instructions; applies to subsequent turns):\n```diff\n{unifiedDiff}\n```";
        return diffMessage.Length < current.Length
            ? diffMessage
            : $"Updated system instructions (supersedes earlier conflicting instructions for subsequent turns):\n{current}";
    }

    private static bool HasSurvivingLines(DiffResult diffResult)
    {
        if (diffResult.PiecesOld.Count == 0)
        {
            return false;
        }

        var deletedLines = 0;
        foreach (var block in diffResult.DiffBlocks)
        {
            deletedLines += block.DeleteCountA;
        }

        return deletedLines < diffResult.PiecesOld.Count;
    }

    private static string FormatUnifiedDiff(DiffResult diffResult)
    {
        var piecesOld = diffResult.PiecesOld;
        var piecesNew = diffResult.PiecesNew;
        var blocks = diffResult.DiffBlocks;
        var builder = new StringBuilder();

        var groupStart = 0;
        while (groupStart < blocks.Count)
        {
            var groupEnd = groupStart;
            while (groupEnd + 1 < blocks.Count)
            {
                var currentHunkEnd = blocks[groupEnd].DeleteStartA + blocks[groupEnd].DeleteCountA + DiffContextLines;
                if (blocks[groupEnd + 1].DeleteStartA - DiffContextLines > currentHunkEnd)
                {
                    break;
                }

                groupEnd++;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            AppendHunk(builder, piecesOld, piecesNew, blocks, groupStart, groupEnd);
            groupStart = groupEnd + 1;
        }

        return builder.ToString();
    }

    private static void AppendHunk(
        StringBuilder builder,
        IReadOnlyList<string> piecesOld,
        IReadOnlyList<string> piecesNew,
        IList<DiffBlock> blocks,
        int groupStart,
        int groupEnd
    )
    {
        var first = blocks[groupStart];
        var last = blocks[groupEnd];
        var startA = Math.Max(0, first.DeleteStartA - DiffContextLines);
        var startB = Math.Max(0, first.InsertStartB - DiffContextLines);
        var endA = Math.Min(piecesOld.Count, last.DeleteStartA + last.DeleteCountA + DiffContextLines);
        var endB = Math.Min(piecesNew.Count, last.InsertStartB + last.InsertCountB + DiffContextLines);

        builder
            .Append("@@ -")
            .Append(startA + 1)
            .Append(',')
            .Append(endA - startA)
            .Append(" +")
            .Append(startB + 1)
            .Append(',')
            .Append(endB - startB)
            .Append(" @@");

        var cursorA = startA;
        for (var i = groupStart; i <= groupEnd; i++)
        {
            var block = blocks[i];
            while (cursorA < block.DeleteStartA)
            {
                builder.Append("\n ").Append(piecesOld[cursorA]);
                cursorA++;
            }

            for (var d = 0; d < block.DeleteCountA; d++)
            {
                builder.Append("\n-").Append(piecesOld[cursorA]);
                cursorA++;
            }

            for (var ins = 0; ins < block.InsertCountB; ins++)
            {
                builder.Append("\n+").Append(piecesNew[block.InsertStartB + ins]);
            }
        }

        while (cursorA < endA)
        {
            builder.Append("\n ").Append(piecesOld[cursorA]);
            cursorA++;
        }
    }

    private static string FormatSystemReminder(List<string> lines)
    {
        var builder = new StringBuilder("<system-reminder>\n");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(lines[i]);
        }

        builder.Append("\n</system-reminder>");
        return builder.ToString();
    }

    private static MessageHash ComputeMessageHash(ChatMessage message)
    {
        var hasher = new DualFnv1aHasher();
        hasher.AddTag(0x01);
        hasher.AddString(message.Role.Value);
        hasher.AddInt32(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            HashContent(ref hasher, content);
        }

        return hasher.Finish();
    }

    private static void HashContent(ref DualFnv1aHasher hasher, AIContent content)
    {
        switch (content)
        {
            case TextContent text:
                hasher.AddTag(0x10);
                hasher.AddString(text.Text);
                break;
            case TextReasoningContent reasoning:
                hasher.AddTag(0x11);
                hasher.AddString(reasoning.Text);
                hasher.AddString(reasoning.ProtectedData);
                HashAdditionalProperties(ref hasher, reasoning.AdditionalProperties);
                break;
            case FunctionCallContent call:
                hasher.AddTag(0x12);
                hasher.AddString(call.CallId);
                hasher.AddString(call.Name);
                HashDictionary(ref hasher, call.Arguments);
                HashAdditionalProperties(ref hasher, call.AdditionalProperties);
                break;
            case FunctionResultContent result:
                hasher.AddTag(0x13);
                hasher.AddString(result.CallId);
                HashObject(ref hasher, result.Result);
                break;
            case DataContent data:
                hasher.AddTag(0x14);
                hasher.AddString(data.MediaType);
                hasher.AddBytes(data.Data.Span);
                break;
            case UriContent uri:
                hasher.AddTag(0x15);
                hasher.AddString(uri.MediaType);
                hasher.AddString(uri.Uri.AbsoluteUri);
                break;
            default:
                hasher.AddTag(0x1F);
                hasher.AddString(content.GetType().FullName);
                hasher.AddString(content.ToString());
                break;
        }
    }

    private static void HashAdditionalProperties(ref DualFnv1aHasher hasher, AdditionalPropertiesDictionary? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            hasher.AddTag(0x20);
            return;
        }

        hasher.AddTag(0x21);
        hasher.AddInt32(properties.Count);
        foreach (var pair in properties.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            hasher.AddString(pair.Key);
            HashObject(ref hasher, pair.Value);
        }
    }

    private static void HashDictionary(ref DualFnv1aHasher hasher, IDictionary<string, object?>? values)
    {
        if (values is null || values.Count == 0)
        {
            hasher.AddTag(0x22);
            return;
        }

        hasher.AddTag(0x23);
        hasher.AddInt32(values.Count);
        foreach (var pair in values.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            hasher.AddString(pair.Key);
            HashObject(ref hasher, pair.Value);
        }
    }

    private static void HashObject(ref DualFnv1aHasher hasher, object? value)
    {
        switch (value)
        {
            case null:
                hasher.AddTag(0x30);
                break;
            case string text:
                hasher.AddTag(0x31);
                hasher.AddString(text);
                break;
            case bool boolean:
                hasher.AddTag(boolean ? (byte)0x32 : (byte)0x33);
                break;
            case int i:
                hasher.AddTag(0x34);
                hasher.AddInt32(i);
                break;
            case long l:
                hasher.AddTag(0x35);
                hasher.AddInt64(l);
                break;
            case ModelToolContent toolContent:
                hasher.AddTag(toolContent.IsError ? (byte)0x36 : (byte)0x37);
                hasher.AddInt32(toolContent.Count);
                foreach (var part in toolContent)
                {
                    HashContent(ref hasher, part);
                }

                break;
            case JsonElement element:
                hasher.AddTag(0x38);
                hasher.AddString(element.GetRawText());
                break;
            case JsonNode node:
                hasher.AddTag(0x39);
                hasher.AddString(node.ToJsonString());
                break;
            case IConvertible convertible:
                hasher.AddTag(0x3A);
                hasher.AddString(convertible.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                hasher.AddTag(0x3B);
                hasher.AddString(SerializeFallback(value));
                break;
        }
    }

    private static string SerializeFallback(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static class EmptyStringSet
    {
        public static HashSet<string> Instance { get; } = new(0, StringComparer.Ordinal);
    }

    private static class EmptyReminderMap
    {
        public static Dictionary<string, ContextTransientReminder> Instance { get; } = new(0, StringComparer.Ordinal);
    }

    private sealed record ToolFingerprint(string? PluginKey, string Description, string SchemaRawText);

    private sealed record PrefixReminderEntry(
        int AfterMessageCount,
        string Content,
        string AppliedSystemPrompt,
        HashSet<string> AppliedDisabledTools,
        Dictionary<string, ContextTransientReminder> AppliedTransientReminders
    );

    private sealed class EpochSnapshot(
        ContextPrefixIdentity identity,
        bool hasSystemHead,
        string initialSystemPrompt,
        AIFunctionDeclaration[] lockedTools,
        Dictionary<string, ToolFingerprint> toolFingerprints,
        MessageHash[] initialHashes,
        DateTimeOffset createdAt
    )
    {
        public ContextPrefixIdentity Identity { get; } = identity;
        public bool HasSystemHead { get; } = hasSystemHead;
        public string InitialSystemPrompt { get; } = initialSystemPrompt;
        public AIFunctionDeclaration[] LockedTools { get; } = lockedTools;
        public string[] LockedToolNames { get; } = [.. lockedTools.Select(static t => t.Name)];
        public Dictionary<string, ToolFingerprint> ToolFingerprints { get; } = toolFingerprints;
        public List<PrefixReminderEntry> Reminders { get; } = [];
        public MessageHash[] LastRequestMessageHashes { get; set; } = initialHashes;
        public DateTimeOffset LastUsedAt { get; set; } = createdAt;
    }

    private readonly record struct MessageHash(ulong High, ulong Low);

    private struct DualFnv1aHasher()
    {
        private ulong _h1 = 14695981039346656037UL;
        private ulong _h2 = 1099511628211UL ^ 0x9E3779B97F4A7C15UL;

        public void AddTag(byte tag)
        {
            const ulong prime1 = 1099511628211UL;
            const ulong prime2 = 1099511628211UL;
            _h1 = (_h1 ^ tag) * prime1;
            _h2 = (_h2 ^ tag) * prime2 ^ 0x517CC1B727220A95UL;
        }

        public void AddInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            MemoryMarshal.Write(bytes, in value);
            AddBytes(bytes);
        }

        public void AddInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            MemoryMarshal.Write(bytes, in value);
            AddBytes(bytes);
        }

        public void AddString(string? value)
        {
            if (value is null)
            {
                AddInt32(-1);
                return;
            }

            AddInt32(value.Length);
            AddBytes(MemoryMarshal.AsBytes(value.AsSpan()));
        }

        public void AddBytes(ReadOnlySpan<byte> bytes)
        {
            const ulong prime = 1099511628211UL;
            foreach (var b in bytes)
            {
                _h1 = (_h1 ^ b) * prime;
                _h2 = (_h2 + b) * prime ^ _h1 >> 17;
            }
        }

        public readonly MessageHash Finish() => new(_h1, _h2);
    }
}
