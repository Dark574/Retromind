using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.ViewModels;

public class ViewModelBase : ObservableObject
{
    // Die Methoden SetProperty und OnPropertyChanged kommen jetzt automatisch von ObservableObject.
    // Wir brauchen hier also eigentlich gar nichts mehr reinschreiben,
    // außer wir wollen eigene Helper.
}