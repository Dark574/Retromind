using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task ShowGogDlcManagerAsync(MediaItem item, Window owner)
    {
        var gameId = GogMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(gameId))
            return;

        var viewModel = new GogDlcDialogViewModel(_gogInstallService, gameId);
        var dialog = new GogDlcDialogView { DataContext = viewModel };
        await dialog.ShowDialog(owner);
    }
}
