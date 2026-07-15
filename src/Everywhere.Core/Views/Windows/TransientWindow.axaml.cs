using ShadUI;

namespace Everywhere.Views;

public sealed partial class TransientWindow : ShadWindow
{
    public TransientWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Its content should be null before closing to make it detach from the visual tree.
        // Otherwise, it will try to attach to the visual tree again (Exception).
        Content = null;
        base.OnClosed(e);
    }
}