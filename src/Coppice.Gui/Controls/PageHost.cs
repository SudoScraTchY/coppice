using Avalonia;
using Avalonia.Controls;
using Coppice.Gui.ViewModels;
using Coppice.Gui.Views;

namespace Coppice.Gui.Controls;

/// <summary>
/// Swaps the visible page and creates each page the first time it is visited. The shell starts with
/// one view loaded instead of eight, which keeps the first frame cheap.
/// </summary>
public sealed class PageHost : ContentControl
{
    public static readonly StyledProperty<PageKey> PageProperty =
        AvaloniaProperty.Register<PageHost, PageKey>(nameof(Page));

    private readonly Dictionary<PageKey, Control> _pages = [];

    public PageKey Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PageProperty)
        {
            Show(change.GetNewValue<PageKey>());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The binding may set the same value as the property default at startup, which raises no
        // change notification. The first attached page therefore has to be created here.
        if (Content is null)
        {
            Show(Page);
        }
    }

    private void Show(PageKey page)
    {
        if (!_pages.TryGetValue(page, out Control? view))
        {
            view = page switch
            {
                PageKey.Setup => new SetupView(),
                PageKey.Scan => new ScanView(),
                PageKey.Review => new ReviewView(),
                PageKey.Apply => new ApplyView(),
                PageKey.Quarantine => new QuarantineView(),
                PageKey.Items => new ItemsView(),
                PageKey.Roots => new RootsView(),
                PageKey.Problems => new ProblemsView(),
                _ => new SetupView(),
            };

            _pages[page] = view;
        }

        Content = view;
    }
}
