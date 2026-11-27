using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class MediaItemContextMenu : ContextMenu
{
    public MediaItemContextMenu() => AvaloniaXamlLoader.Load(this);

    protected override Type StyleKeyOverride => typeof(ContextMenu);
}

// A fresh scope per opening keeps commands and live item-state bindings explicit,
// including across popup roots. Clearing DataContext releases the entire scope.
internal sealed record MediaItemMenuContext(MainWindowViewModel Commands, MediaItem Item, MediaNode? Node)
{
    public bool HasLibraryActions => Node != null;
}
