using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Coppice.Gui.ViewModels;

namespace Coppice.Gui.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } provider)
        {
            viewModel.ReportFolderPickerError("A folder picker is not available on this desktop.");
            return;
        }

        try
        {
            IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a project folder",
                AllowMultiple = true,
            });

            viewModel.AddProjectRoots(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception ex)
        {
            viewModel.ReportFolderPickerError($"The folder picker failed: {ex.Message}");
        }
    }
}
