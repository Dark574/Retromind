using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;

namespace Retromind.Views;

internal static class DesktopDialogs
{
    internal static async Task<bool> ShowConfirmAsync(Window owner, string message)
    {
        var dialog = new ConfirmView { DataContext = message };
        var result = await dialog.ShowDialog<bool>(owner);
        return result;
    }

    internal static async Task ShowInfoAsync(Window owner, string message, bool showCopyButton = true)
    {
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var dialog = new InfoView
            {
                DataContext = message,
                ShowCopyButton = showCopyButton
            };
            await dialog.ShowDialog<bool>(owner);
        });
    }
}
