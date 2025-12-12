using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind;

public partial class MainWindow : Window
{
    // Flag um zu prüfen, ob wir schon gespeichert haben
    private bool _canClose = false;
    
    public MainWindow()
    {
        InitializeComponent();
    }

    // Wir überschreiben die Methode, die beim Schließen aufgerufen wird
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Wenn wir schon gespeichert haben (2. Durchlauf), lassen wir das Fenster zugehen.
        if (_canClose) return;

        // 1. Durchlauf: Wir brechen das Schließen ab!
        e.Cancel = true;

        // Wir holen uns das ViewModel
        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                // WICHTIG: Wir warten, bis das Speichern wirklich fertig ist (File Handle geschlossen)
                await vm.SaveData();
                vm.Cleanup(); // Musik stoppen etc.
            }
            catch
            {
                // Falls das Speichern crasht, ignorieren wir es hier, damit die App trotzdem schließt
                // und nicht "hängen bleibt".
            }
        }

        // Jetzt setzen wir das Flag auf wahr
        _canClose = true;
        
        // ÄNDERUNG: Wir schließen nicht sofort, sondern geben dem UI Thread
        // kurz Luft, um alle Events abzuarbeiten. Das hilft oft gegen DBus Fehler.
        Dispatcher.UIThread.Post(() => 
        {
            Close();
        }, DispatcherPriority.Background);
    }

    // Event Handler für das Draggen
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Unterscheiden zwischen Klick und Drag
        // Doppelklick zum Maximieren
        if (e.ClickCount == 2)
            ToggleWindowState();
        else
            BeginMoveDrag(e);
    }

    public void MinimizeWindow()
    {
        WindowState = WindowState.Minimized;
    }

    public void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void CloseWindow()
    {
        Close();
    }

    // Resizing Logic
    private void OnResizeDrag(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag is WindowEdge edge) BeginResizeDrag(edge, e);
    }
    
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Wir suchen das BigModeViewModel.
        // Es kann entweder direkt der Content sein (ViewModel-First) 
        // ODER im DataContext eines Controls stecken (View-First).
        BigModeViewModel? bigVm = null;

        if (vm.FullScreenContent is BigModeViewModel directVm)
        {
            // Fall 1: Der Content IST das ViewModel
            bigVm = directVm;
        }
        else if (vm.FullScreenContent is Control { DataContext: BigModeViewModel contextVm })
        {
            // Fall 2: Der Content ist ein Control, das das ViewModel hält
            bigVm = contextVm;
        }

        // Wenn wir das ViewModel gefunden haben, führen wir die Befehle aus
        if (bigVm != null)
        {
            switch (e.Key)
            {
                case Key.Up:
                    bigVm.SelectPreviousCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Down:
                    bigVm.SelectNextCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    bigVm.PlayCurrentCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                case Key.Back:
                    bigVm.ExitBigModeCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}