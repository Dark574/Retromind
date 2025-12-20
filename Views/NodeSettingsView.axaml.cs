using System;
using Avalonia.Controls;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class NodeSettingsView : Window
{
    public NodeSettingsView()
    {
        InitializeComponent();
    }
    
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // inject Provider
        if (DataContext is NodeSettingsViewModel vm)
        {
            vm.StorageProvider = this.StorageProvider;
        }
    }
}