using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Coppice.Gui.Services;
using Coppice.Gui.ViewModels;
using Coppice.Gui.Views;

namespace Coppice.Gui;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GuiPreferences preferences = GuiPreferences.Load(GuiServices.CreatePreferences());
            RequestedThemeVariant = preferences.Theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

            GuiServices services = GuiServices.Create();
            var viewModel = new MainWindowViewModel(services, preferences);
            var window = new MainWindow { DataContext = viewModel };

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.PersistPreferences();
                services.Dispose();
            };

            // Let the shell paint before touching the snapshot or the filesystem. The loading state
            // inside the window is what the user sees during the first read.
            window.Opened += (_, _) =>
                Dispatcher.UIThread.Post(
                    () => _ = viewModel.InitializeAsync(),
                    DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
