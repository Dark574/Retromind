using Avalonia.Controls;
using Avalonia.Interactivity;
using Retromind.Resources;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class SettingsView : Window
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnPortableHomeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
            return;

        if (DataContext is not SettingsViewModel vm)
            return;

        var requestedEnabled = checkBox.IsChecked == true;
        if (!requestedEnabled)
        {
            vm.SetPortableHomeInAppImageMode(enabled: false, force: false);
            checkBox.IsChecked = vm.UsePortableHomeInAppImage;
            return;
        }

        var warningMessage = Strings.ResourceManager.GetString("Settings.PortableHome.WarningForceConfirm")
                             ?? Strings.Settings_PortableHome_Hint;

        var confirm = new ConfirmView
        {
            DataContext = warningMessage
        };

        var confirmed = await confirm.ShowDialog<bool>(this);
        vm.SetPortableHomeInAppImageMode(enabled: confirmed, force: confirmed);
        checkBox.IsChecked = vm.UsePortableHomeInAppImage;
    }
}
