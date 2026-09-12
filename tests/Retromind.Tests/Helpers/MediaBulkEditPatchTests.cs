using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class MediaBulkEditPatchTests
{
    [Fact]
    public void ApplyTo_ChangesOnlyExplicitProperties()
    {
        var item = new MediaItem("Original")
        {
            Status = PlayStatus.Incomplete,
            Developer = "Developer"
        };
        item.CustomFields["Region"] = "PAL";

        var patch = new MediaBulkEditPatch
        {
            ChangeStatus = true,
            Status = PlayStatus.Completed
        };

        Assert.True(patch.ApplyTo(item));
        Assert.Equal(PlayStatus.Completed, item.Status);
        Assert.Equal("Original", item.Title);
        Assert.Equal("Developer", item.Developer);
        Assert.Equal("PAL", item.CustomFields["Region"]);
    }

    [Fact]
    public void ApplyTo_SetsFieldCaseInsensitivelyAndPreservesInternalFields()
    {
        var item = new MediaItem("Item");
        item.CustomFields["Region"] = "PAL";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "42";

        var patch = new MediaBulkEditPatch
        {
            CustomFields =
            [
                new CustomFieldPatch(CustomFieldPatchOperation.Set, " region ", " NTSC ")
            ]
        };

        Assert.True(patch.ApplyTo(item));
        Assert.Equal("NTSC", item.CustomFields["Region"]);
        Assert.False(item.CustomFields.ContainsKey("region"));
        Assert.Equal("42", item.CustomFields[CustomFieldKeyHelper.StoreGameId]);
    }

    [Fact]
    public void ApplyTo_RemovesAllCaseVariantsOfField()
    {
        var item = new MediaItem("Item");
        item.CustomFields["Region"] = "PAL";
        item.CustomFields["REGION"] = "NTSC";

        var patch = new MediaBulkEditPatch
        {
            CustomFields =
            [
                new CustomFieldPatch(CustomFieldPatchOperation.Remove, "region")
            ]
        };

        Assert.True(patch.ApplyTo(item));
        Assert.DoesNotContain(item.CustomFields.Keys, key =>
            string.Equals(key, "region", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyTo_ReturnsFalseWhenValuesAlreadyMatch()
    {
        var item = new MediaItem("Item") { Status = PlayStatus.Completed };
        item.CustomFields["Region"] = "PAL";

        var patch = new MediaBulkEditPatch
        {
            ChangeStatus = true,
            Status = PlayStatus.Completed,
            CustomFields =
            [
                new CustomFieldPatch(CustomFieldPatchOperation.Set, "Region", "PAL")
            ]
        };

        Assert.False(patch.ApplyTo(item));
    }
}
