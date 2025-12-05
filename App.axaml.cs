using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Retromind.ViewModels;

namespace Retromind;

public class App : Application
{
    public override void Initialize()
    {
        //Für Tests von englischer Sprache
        //Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 1. ViewModel erstellen
            var vm = new MainWindowViewModel();

            // 2. Window erstellen und DataContext setzen
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };

            // 3. Beim Beenden (Exit) speichern
            desktop.Exit += (sender, args) =>
            {
                // Hier wird gespeichert UND Musik gestoppt
                vm.Cleanup();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}