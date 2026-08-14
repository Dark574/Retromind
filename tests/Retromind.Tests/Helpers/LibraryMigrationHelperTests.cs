using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class LibraryMigrationHelperTests
{
    [Fact]
    public void MigrateLaunchPaths_ConvertsPortableFieldsAndLeavesExternalValuesUntouched()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);
        var item = CreateFullyConfiguredItem(portableRoot, externalRoot);
        var node = new MediaNode
        {
            NativeWrappersOverride =
            [
                new LaunchWrapper { Path = portableRoot.GetPath("Tools", "node-wrapper") }
            ],
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["PROTONPATH"] = portableRoot.GetPath("Emulators", "GE-Proton"),
                ["WINEDEBUG"] = portableRoot.GetPath("This", "is", "not", "a", "path-setting")
            }
        };
        node.Items.Add(item);
        var roots = new ObservableCollection<MediaNode> { node };

        var migrated = LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(roots);

        Assert.Equal(9, migrated);
        Assert.Equal(MediaFileKind.LibraryRelative, item.Files[0].Kind);
        Assert.Equal(Path.Combine("Library", "Games", "Portable", "disc1.iso"), item.Files[0].Path);
        Assert.Equal(MediaFileKind.Absolute, item.Files[1].Kind);
        Assert.Equal(externalRoot.GetPath("external.iso"), item.Files[1].Path);
        Assert.Equal(Path.Combine("Tools", "launcher"), item.LauncherPath);
        Assert.Equal(Path.Combine("Library", "Games", "Portable"), item.WorkingDirectory);
        Assert.Equal(Path.Combine("Home", ".config", "portable-game"), item.XdgConfigPath);
        Assert.Equal(Path.Combine("Prefixes", "Portable_Prefix"), item.PrefixPath);
        Assert.Equal(Path.Combine("Tools", "item-wrapper"), item.NativeWrappersOverride![0].Path);
        Assert.Equal(Path.Combine("Home", "portable-game"), item.EnvironmentOverrides["HOME"]);
        Assert.Equal(Path.Combine("Tools", "node-wrapper"), node.NativeWrappersOverride![0].Path);
        Assert.Equal(Path.Combine("Emulators", "GE-Proton"), node.EnvironmentOverrides!["PROTONPATH"]);
        Assert.Equal(
            portableRoot.GetPath("This", "is", "not", "a", "path-setting"),
            node.EnvironmentOverrides["WINEDEBUG"]);
        Assert.Equal(0, LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(roots));
    }

    [Fact]
    public void MigrateLaunchPaths_RebindsConvertedFileAndPrefixAfterRootMove()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        var item = new MediaItem
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = firstRoot.GetPath("Library", "Games", "Portable", "game.exe")
                }
            ],
            PrefixPath = firstRoot.GetPath("Library", "Prefixes", "Portable_Prefix")
        };
        var node = new MediaNode();
        node.Items.Add(item);

        using (UseDataRoot(firstRoot.RootPath))
        {
            Assert.Equal(
                2,
                LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(
                    new ObservableCollection<MediaNode> { node }));
        }

        using (UseDataRoot(secondRoot.RootPath))
        {
            Assert.Equal(
                secondRoot.GetPath("Library", "Games", "Portable", "game.exe"),
                item.GetPrimaryLaunchPath());
            Assert.Equal(
                secondRoot.GetPath("Library", "Prefixes", "Portable_Prefix"),
                Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, item.PrefixPath!)));
        }
    }

    [Fact]
    public void MigrateLaunchPaths_DoesNotConvertDifferentlyCasedLinuxRoot()
    {
        using var portableRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);
        var differentlyCasedRoot = Path.Combine(
            Path.GetDirectoryName(portableRoot.RootPath)!,
            Path.GetFileName(portableRoot.RootPath).ToUpperInvariant());
        var launchPath = Path.Combine(differentlyCasedRoot, "Library", "Games", "game.exe");
        var prefixPath = Path.Combine(differentlyCasedRoot, "Library", "Prefixes", "Game");
        var item = new MediaItem
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = launchPath
                }
            ],
            PrefixPath = prefixPath
        };
        var node = new MediaNode();
        node.Items.Add(item);

        var migrated = LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(
            new ObservableCollection<MediaNode> { node });

        Assert.Equal(0, migrated);
        Assert.Equal(MediaFileKind.Absolute, item.Files[0].Kind);
        Assert.Equal(launchPath, item.Files[0].Path);
        Assert.Equal(prefixPath, item.PrefixPath);
    }

    private static MediaItem CreateFullyConfiguredItem(
        TemporaryDirectory portableRoot,
        TemporaryDirectory externalRoot)
    {
        var item = new MediaItem
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = portableRoot.GetPath("Library", "Games", "Portable", "disc1.iso")
                },
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = externalRoot.GetPath("external.iso")
                }
            ],
            LauncherPath = portableRoot.GetPath("Tools", "launcher"),
            WorkingDirectory = portableRoot.GetPath("Library", "Games", "Portable"),
            XdgConfigPath = portableRoot.GetPath("Home", ".config", "portable-game"),
            PrefixPath = portableRoot.GetPath("Library", "Prefixes", "Portable_Prefix"),
            NativeWrappersOverride =
            [
                new LaunchWrapper { Path = portableRoot.GetPath("Tools", "item-wrapper") }
            ]
        };
        item.EnvironmentOverrides["HOME"] = portableRoot.GetPath("Home", "portable-game");
        return item;
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
