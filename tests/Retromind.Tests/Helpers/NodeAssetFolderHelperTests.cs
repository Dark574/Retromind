using System.ComponentModel;
using System.Text.Json;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class NodeAssetFolderHelperTests
{
    [Fact]
    public void Rename_MovesNodeItemAndActiveAssetsAndPreservesUnrelatedReferences()
    {
        using var state = new AssetState();
        var nodeAsset = state.AddAsset(state.Node, "Logo", "Node_Logo_01.png");
        var item = new MediaItem("Game");
        state.Node.Items.Add(item);
        var first = state.AddAsset(item, "Cover", "Game_Cover_01.png");
        var active = state.AddAsset(item, "Cover", "Game_Cover_02.png");
        item.SetActiveAsset(AssetType.Cover, active.RelativePath);
        state.Node.SetActiveAsset(AssetType.Logo, nodeAsset.RelativePath);
        var unrelated = new MediaAsset { Type = AssetType.Video, RelativePath = "Library/Elsewhere/Video/other.mp4" };
        item.Assets.Add(unrelated);
        item.SetActiveAsset(AssetType.Video, unrelated.RelativePath);

        var result = state.Move();

        Assert.True(result.Success);
        Assert.Equal("New", state.Node.Name);
        Assert.Equal("Library/New/Logo/Node_Logo_01.png", nodeAsset.RelativePath);
        Assert.Equal("Library/New/Cover/Game_Cover_01.png", first.RelativePath);
        Assert.Equal("Library/New/Cover/Game_Cover_02.png", item.GetPrimaryAssetPath(AssetType.Cover));
        Assert.Equal(nodeAsset.RelativePath, state.Node.PrimaryLogoPath);
        Assert.Equal("Library/Elsewhere/Video/other.mp4", unrelated.RelativePath);
        Assert.Equal(unrelated.RelativePath, item.GetPrimaryAssetPath(AssetType.Video));
        Assert.True(File.Exists(active.AbsolutePath));
        Assert.False(Directory.Exists(state.OldFolder));
        Assert.Empty(result.CleanupFailures);
    }

    [Fact]
    public void Move_IncludesChildAssetsEvenWhenTheRootHasNoAssetFolders()
    {
        using var state = new AssetState();
        var child = new MediaNode("Child");
        state.Node.Children.Add(child);
        var path = state.Temp.CreateFile("Library/Old/Child/Cover/Child_Cover_01.png", "child");
        child.Assets.Add(new MediaAsset { Type = AssetType.Cover, RelativePath = NodeAssetFolderHelper.RelativeLocation(path) });
        var result = NodeAssetFolderHelper.MoveAndUpdateAssetPaths(state.Node, ["Old"], ["Parent", "Old"], () => { }, () => { });
        Assert.True(result.Success);
        Assert.Equal("Library/Parent/Old/Child/Cover/Child_Cover_01.png", child.Assets[0].RelativePath);
        Assert.True(File.Exists(child.Assets[0].AbsolutePath));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Collision_UsesExistingNumberingAndUpdatesTheExactRenamedReference()
    {
        using var state = new AssetState();
        var asset = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        var existing = state.Temp.CreateFile("Library/New/Cover/Game_Cover_01.png", "foreign");
        var result = state.Move();
        Assert.True(result.Success);
        Assert.Equal("Library/New/Cover/Game_Cover_02.png", asset.RelativePath);
        Assert.Equal("ours", File.ReadAllText(asset.AbsolutePath));
        Assert.Equal("foreign", File.ReadAllText(existing));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("shared")]
    [InlineData("depth")]
    public void InvalidTraversal_ChangesNeitherMetadataNorFiles(string invalid)
    {
        using var state = new AssetState();
        var asset = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        var before = asset.RelativePath;
        if (invalid == "cycle") state.Node.Children.Add(state.Node);
        else if (invalid == "shared")
        {
            var child = new MediaNode("Child");
            state.Node.Children.Add(child);
            state.Node.Children.Add(child);
        }
        else
        {
            var current = state.Node;
            for (var index = 0; index < 257; index++)
            {
                var child = new MediaNode("Child");
                current.Children.Add(child);
                current = child;
            }
        }
        var writes = 0;
        var updated = NodeAssetFolderHelper.UpdateAssetPathsRecursive(state.Node, "Library/Old", "Library/New", null);
        var moved = state.Move(new NodeAssetFileOperations { MoveFile = (_, _) => writes++ });
        Assert.False(updated.Success);
        Assert.False(moved.Success);
        Assert.Equal("NodeAssets.TraversalFailed", moved.FailureCode);
        Assert.Equal(0, writes);
        Assert.Equal(before, asset.RelativePath);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.False(Directory.Exists(state.NewFolder));
    }

    [Fact]
    public void PathUpdate_FullyPreparesBeforeMutatingAndRollsBackSetterFailures()
    {
        using var state = new AssetState();
        var first = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        var second = state.AddAsset(state.Node, "Cover", "Game_Cover_02.png");
        var original = second.RelativePath;
        second.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaAsset.RelativePath) && second.RelativePath.StartsWith("Library/New/"))
                throw new IOException("Fixture observer failed");
        };
        var result = NodeAssetFolderHelper.UpdateAssetPathsRecursive(state.Node, "Library/Old", "Library/New", null);
        Assert.False(result.Success);
        Assert.Equal("NodeAssets.PathUpdateFailed", result.FailureCode);
        Assert.True(result.RollbackComplete);
        Assert.Equal(original, second.RelativePath);
        Assert.StartsWith("Library/Old/", first.RelativePath);
        Assert.Equal(first.RelativePath, state.Node.PrimaryCoverPath);
    }

    [Fact]
    public void CommitFailure_RollsBackBothStagedAndAlreadyCommittedFiles()
    {
        using var state = new AssetState();
        var first = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        var second = state.AddAsset(state.Node, "Cover", "Game_Cover_02.png");
        var commits = 0;
        var result = state.Move(new NodeAssetFileOperations { MoveFile = (source, target) =>
        {
            if (target.StartsWith(state.NewFolder) && ++commits == 2) throw new IOException("Fixture commit failed");
            File.Move(source, target);
        } });
        Assert.False(result.Success);
        Assert.Equal("NodeAssets.MoveFailed", result.FailureCode);
        Assert.True(result.RollbackComplete);
        Assert.Equal("Old", state.Node.Name);
        Assert.Equal("ours", File.ReadAllText(first.AbsolutePath));
        Assert.Equal("ours", File.ReadAllText(second.AbsolutePath));
        Assert.Empty(result.RecoveryFiles);
        Assert.False(Directory.Exists(state.Temp.GetPath("Library", ".retromind_asset_move_staging")));
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("category")]
    public void FailureAfterFilePublication_RestoresFilesPathsAndCategoryName(string phase)
    {
        using var state = new AssetState();
        var asset = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        if (phase == "paths") asset.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaAsset.RelativePath) && asset.RelativePath.StartsWith("Library/New/"))
                throw new IOException("Fixture path update failed");
        };
        else state.Node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaNode.Name) && state.Node.Name == "New")
                throw new IOException("Fixture category publication failed");
        };
        var result = state.Move();
        Assert.False(result.Success);
        Assert.Equal("NodeAssets.PathUpdateFailed", result.FailureCode);
        Assert.True(result.RollbackComplete);
        Assert.Equal("Old", state.Node.Name);
        Assert.StartsWith("Library/Old/", asset.RelativePath);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.False(File.Exists(state.Temp.GetPath("Library/New/Cover/Game_Cover_01.png")));
    }

    [Fact]
    public void IncompleteRollback_PreservesCompetingSourceAndCommittedOriginalAndWritesRecoveryManifest()
    {
        using var state = new AssetState();
        state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        state.AddAsset(state.Node, "Cover", "Game_Cover_02.png");
        string? firstTarget = null;
        string? originalSource = null;
        var result = state.Move(new NodeAssetFileOperations { MoveFile = (source, target) =>
        {
            if (target.StartsWith(state.NewFolder))
            {
                if (firstTarget == null)
                {
                    firstTarget = target;
                    originalSource = Path.Combine(state.OldFolder, "Cover", Path.GetFileName(target));
                }
                else
                {
                    File.WriteAllText(originalSource!, "competing file");
                    throw new IOException("token=must-not-be-reported");
                }
            }
            File.Move(source, target);
        } });
        Assert.False(result.Success);
        Assert.False(result.RollbackComplete);
        Assert.Equal("competing file", File.ReadAllText(originalSource!));
        Assert.Equal("ours", File.ReadAllText(firstTarget!));
        Assert.NotEmpty(result.RecoveryFiles);
        Assert.NotNull(result.RecoveryManifest);
        using var manifest = JsonDocument.Parse(File.ReadAllText(AppPaths.ResolveDataPath(result.RecoveryManifest)));
        Assert.Equal(result.RecoveryFiles.Count, manifest.RootElement.GetProperty("RecoveryFiles").GetArrayLength());
        var errors = new RecordingErrorService();
        NodeAssetDiagnostics.Report(errors, result, "Move", state.Node.Id);
        Assert.Equal(["NodeAssets.MoveFailed", "NodeAssets.RollbackIncomplete"], errors.Reports.Select(report => report.Code));
        Assert.Equal([ErrorSeverity.Error, ErrorSeverity.Critical], errors.Reports.Select(report => report.Severity));
        Assert.All(errors.Reports, report =>
        {
            Assert.Null(report.Exception);
            Assert.Equal("Move", report.Context!["Operation"]);
            Assert.Equal(result.RecoveryManifest, report.Context["RecoveryManifest"]);
        });
        Assert.DoesNotContain("must-not-be-reported", JsonSerializer.Serialize(errors.Reports));
        Assert.DoesNotContain(state.Temp.RootPath, JsonSerializer.Serialize(errors.Reports));
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public void CleanupFailure_IsDiagnosticAndDoesNotTurnSuccessfulMoveIntoFailure()
    {
        using var state = new AssetState();
        var asset = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        var result = state.Move(new NodeAssetFileOperations { DeleteDirectory = path =>
        {
            if (path == Path.Combine(state.OldFolder, "Cover")) throw new IOException("Fixture cleanup failed");
            Directory.Delete(path);
        } });
        Assert.True(result.Success);
        Assert.True(File.Exists(asset.AbsolutePath));
        var errors = new RecordingErrorService { ThrowOnReport = true };
        NodeAssetDiagnostics.Report(errors, result, "Rename", state.Node.Id);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("NodeAssets.CleanupFailed", report.Code);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
    }

    [Fact]
    public void InterveningTreeMutation_IsDetectedBeforeAnyPreparedPathIsPublished()
    {
        using var state = new AssetState();
        var asset = state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
        var before = asset.RelativePath;
        var result = state.Move(new NodeAssetFileOperations { AfterFilesMoved = () => state.Node.Children.Add(state.Node) });
        Assert.False(result.Success);
        Assert.Equal(before, asset.RelativePath);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.True(result.MetadataRecoveryRequired);
        Assert.False(result.RollbackComplete); // The concurrent tree edit is preserved, never silently repaired.
        Assert.NotNull(result.RecoveryManifest);
    }

    [Fact]
    public void TraversalDepth_Allows256AndRejects257BeforeAnySideEffect()
    {
        var root = new MediaNode("Root");
        var current = root;
        for (var index = 0; index < 256; index++)
        {
            var child = new MediaNode("Child");
            current.Children.Add(child);
            current = child;
        }
        Assert.True(NodeAssetFolderHelper.ValidateTraversal(new[] { root }).Success);
        current.Children.Add(new MediaNode("Too deep"));
        var result = NodeAssetFolderHelper.ValidateTraversal(new[] { root });
        Assert.False(result.Success);
        Assert.Equal("MaximumDepthExceeded", result.FailureReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymlinkFolders_AreRejectedBeforeAnyFileOrMetadataChange(bool targetLink)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new AssetState();
        using var external = new TemporaryDirectory();
        var foreign = external.CreateFile("Game_Cover_01.png", "foreign");
        if (targetLink)
        {
            state.AddAsset(state.Node, "Cover", "Game_Cover_01.png");
            Directory.CreateDirectory(state.NewFolder);
            Directory.CreateSymbolicLink(Path.Combine(state.NewFolder, "Cover"), external.RootPath);
        }
        else
        {
            Directory.CreateDirectory(state.OldFolder);
            Directory.CreateSymbolicLink(Path.Combine(state.OldFolder, "Cover"), external.RootPath);
        }
        var result = state.Move();
        Assert.False(result.Success);
        Assert.Equal("Old", state.Node.Name);
        Assert.Equal("foreign", File.ReadAllText(foreign));
    }

    private sealed class AssetState : IDisposable
    {
        public TemporaryDirectory Temp { get; } = new();
        private readonly EnvironmentVariableScope _environment;
        public MediaNode Node { get; } = new("Old");
        public string OldFolder => Temp.GetPath("Library", "Old");
        public string NewFolder => Temp.GetPath("Library", "New");
        public AssetState() => _environment = new(("APPIMAGE", Temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        public MediaAsset AddAsset(object owner, string type, string name)
        {
            var full = Temp.CreateFile(Path.Combine("Library", "Old", type, name), "ours");
            var asset = new MediaAsset { Type = Enum.Parse<AssetType>(type), RelativePath = NodeAssetFolderHelper.RelativeLocation(full) };
            if (owner is MediaNode node) node.Assets.Add(asset);
            else ((MediaItem)owner).Assets.Add(asset);
            return asset;
        }
        public NodeAssetOperationResult Move(NodeAssetFileOperations? io = null) =>
            NodeAssetFolderHelper.MoveAndUpdateAssetPaths(Node, ["Old"], ["New"], () => Node.Name = "New",
                () => Node.Name = "Old", io ?? new NodeAssetFileOperations());
        public void Dispose() { _environment.Dispose(); Temp.Dispose(); }
    }
}
