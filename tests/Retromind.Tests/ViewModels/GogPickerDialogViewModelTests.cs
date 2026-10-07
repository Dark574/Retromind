using Retromind.Models.Stores;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class GogPickerDialogViewModelTests
{
    [Fact]
    public void SingleSelection_SelectingAnotherGameClearsPreviousSelection()
    {
        StoreGameRecord[] games =
        [
            new("gog", "1", "First", "Windows", null),
            new("gog", "2", "Second", "Windows", null)
        ];
        using var viewModel = new GogPickerDialogViewModel(
            games,
            new HashSet<string>(),
            new Dictionary<string, int>(),
            singleSelection: true);

        viewModel.FilteredGames[0].IsSelected = true;
        viewModel.FilteredGames[1].IsSelected = true;

        var selected = Assert.Single(viewModel.GetSelectedGames());
        Assert.Equal("2", selected.StoreGameId);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.False(viewModel.ShowBulkSelectionActions);
        Assert.False(viewModel.ShowOnlyNewInNodeFilter);
    }
}
