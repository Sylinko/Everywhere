using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Presents only inexpensive activity-preview shapes. Heavy controls such as terminals, code
/// editors, diffs, and child chat lists are intentionally not created here.
/// </summary>
public class ChatActivityPreviewPresenter : ContentControl;