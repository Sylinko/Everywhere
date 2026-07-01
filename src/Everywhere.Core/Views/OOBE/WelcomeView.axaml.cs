using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;

namespace Everywhere.Views;

public partial class WelcomeView : ReactiveUserControl<WelcomeViewModel>, IRecipient<ShowConfettiEffectMessage>
{
    public WelcomeView(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        InitializeComponent();
    }

    public void Receive(ShowConfettiEffectMessage message)
    {
        ConfettiEffect?.Start();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        StrongReferenceMessenger.Default.Register(this);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        StrongReferenceMessenger.Default.Unregister<ShowConfettiEffectMessage>(this);
    }
}