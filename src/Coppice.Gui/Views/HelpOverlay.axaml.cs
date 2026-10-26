using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Coppice.Gui.Views;

public partial class HelpOverlay : UserControl
{
    public HelpOverlay()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && IsVisible)
        {
            Dispatcher.UIThread.Post(() => CloseButton.Focus(), DispatcherPriority.Input);
        }
    }
}
