using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Retromind.Resources;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class SettingsView : Window
{
    private bool _closeAfterDownloadCancellation;
    private bool _downloadCancellationForCloseInProgress;
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyboardCaptureKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel != null)
            _viewModel.RequestCopySupportDiagnostics += CopySupportDiagnosticsAsync;
    }

    private async Task<bool> CopySupportDiagnosticsAsync(string text)
    {
        if (Clipboard == null)
            return false;

        await Clipboard.SetTextAsync(text);
        return true;
    }

    private void OnClosed(object? sender, EventArgs e) => DetachViewModel();

    private void DetachViewModel()
    {
        if (_viewModel == null)
            return;

        _viewModel.RequestCopySupportDiagnostics -= CopySupportDiagnosticsAsync;
        _viewModel = null;
    }

    private void OnKeyboardCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel vm &&
            vm.TryCompleteKeyboardCapture(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeAfterDownloadCancellation &&
            DataContext is SettingsViewModel { IsGeReleaseDownloadActive: true } vm)
        {
            e.Cancel = true;
            if (_downloadCancellationForCloseInProgress)
                return;

            _downloadCancellationForCloseInProgress = true;
            await vm.CancelGeReleaseDownloadAsync();
            _closeAfterDownloadCancellation = true;
            Close();
            return;
        }

        base.OnClosing(e);
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
