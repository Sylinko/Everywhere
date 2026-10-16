using Everywhere.Configuration;
using Everywhere.StrategyEngine;

namespace Everywhere.TextSelection;

/// <summary>
/// Pairs a matched strategy with its toolbar-only display settings.
/// </summary>
public sealed record TextSelectionToolbarItem(TextSelectionToolbarAction Action, Strategy Strategy);