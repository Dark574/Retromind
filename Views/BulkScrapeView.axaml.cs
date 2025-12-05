using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class BulkScrapeView : Window
{
    public BulkScrapeView()
    {
        InitializeComponent();
        
        // Wir suchen die TextBox anhand des Namens
        var logBox = this.FindControl<TextBox>("LogBox");
        
        if (logBox != null)
        {
            // Wir hören auf Änderungen am Text-Property
            logBox.PropertyChanged += (sender, args) =>
            {
                if (args.Property == TextBox.TextProperty)
                {
                    // Caret ans Ende setzen -> Scrollt automatisch
                    logBox.CaretIndex = int.MaxValue; 
                }
            };
        }
    }
}