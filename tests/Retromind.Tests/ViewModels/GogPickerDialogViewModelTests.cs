using Retromind.Models.Stores;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class GogPickerDialogViewModelTests
{
    [Fact]
    public void BulkSelection_SelectsEveryNewFilteredGame()
    {
        StoreGameRecord[] games =
        [
            new("gog", "1", "First", "Windows", null),
            new("gog", "2", "Already imported", "Windows", null),
            new("gog", "3", "Third", "Linux", null)
        ];
        using var viewModel = new GogPickerDialogViewModel(
            games,
            new HashSet<string> { "2" },
            new Dictionary<string, int>());

        viewModel.SelectAllFilteredCommand.Execute(null);

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.Equal(
            ["1", "3"],
            viewModel.GetSelectedGames().Select(game => game.StoreGameId).ToArray());
    }

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
