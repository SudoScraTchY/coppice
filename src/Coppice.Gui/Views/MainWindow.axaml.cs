using Avalonia.Controls;
using Coppice.Gui.ViewModels;

namespace Coppice.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.PersistPreferences();
    }
}
