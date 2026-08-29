using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Resources;

namespace Retromind.ViewModels;

/// <summary>
/// Base class for all ViewModels in the application.
/// Inherits from ObservableObject (CommunityToolkit.Mvvm) to provide INotifyPropertyChanged implementation.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    protected static string T(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
