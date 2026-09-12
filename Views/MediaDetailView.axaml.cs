using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Retromind.Views;

public partial class MediaDetailView : UserControl
{
    public MediaDetailView()
    {
        InitializeComponent();

        this.FindControl<ToggleButton>("FavoriteToggle")?.AddHandler(
            PointerReleasedEvent,
            OnFavoriteTogglePointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnFavoriteTogglePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
            return;

        var mainWindow = this.FindAncestorOfType<MainWindow>();
        Dispatcher.UIThread.Post(
            () => mainWindow?.FocusActiveItemArea(),
            DispatcherPriority.Input);
    }
}
