using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.ViewModels;

/// <summary>
/// Base class for all ViewModels in the application.
/// Inherits from ObservableObject (CommunityToolkit.Mvvm) to provide INotifyPropertyChanged implementation.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    // No implementation needed here as ObservableObject provides SetProperty and OnPropertyChanged.
    // Common ViewModel logic (e.g. IsBusy handling) could be added here in the future.
}