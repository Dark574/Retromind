using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class BulkScrapeView : Window
{
    public BulkScrapeView()
    {
        InitializeComponent();
        
        // Find the log TextBox by its name.
        var logBox = this.FindControl<TextBox>("LogBox");
        
        if (logBox != null)
        {
            // Listen to changes on the Text property.
            logBox.PropertyChanged += (sender, args) =>
            {
                if (args.Property == TextBox.TextProperty)
                {
                    // Move the caret to the end -> automatically scrolls to the latest log entry.
                    logBox.CaretIndex = int.MaxValue; 
                }
            };
        }
    }
}