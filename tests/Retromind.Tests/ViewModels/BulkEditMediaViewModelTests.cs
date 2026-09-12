using Retromind.Helpers;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BulkEditMediaViewModelTests
{
    [Fact]
    public void MetadataRow_CompletesKnownLibraryValue()
    {
        var root = new MediaNode("Games", NodeType.Area);
        root.Items.Add(new MediaItem("Game") { Developer = "Nintendo" });
        var viewModel = new BulkEditMediaViewModel(2, [root]);
        var developerRow = Assert.Single(
            viewModel.TextMetadataFields,
            row => row.Field == TextMetadataField.Developer);

        developerRow.Value = "Nin";

        Assert.Equal("tendo", developerRow.SuggestionSuffix);
        Assert.True(developerRow.AcceptSuggestionCommand.CanExecute(null));

        developerRow.AcceptSuggestionCommand.Execute(null);

        Assert.Equal("Nintendo", developerRow.Value);
        Assert.Empty(developerRow.SuggestionSuffix);
    }

    [Fact]
    public void MetadataRow_WithoutSuggestionIndexBehavesLikePlainText()
    {
        var viewModel = new BulkEditMediaViewModel(1);
        var sourceRow = Assert.Single(
            viewModel.TextMetadataFields,
            row => row.Field == TextMetadataField.Source);

        sourceRow.Value = "Imported";

        Assert.Empty(sourceRow.SuggestionSuffix);
        Assert.False(sourceRow.AcceptSuggestionCommand.CanExecute(null));
    }
}
