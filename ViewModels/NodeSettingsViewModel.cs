using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for configuring a <see cref="MediaNode"/>.
/// Supports editing metadata, default emulator selection, theme binding, and node-level assets (cover/logo/wallpaper/video).
/// </summary>
public partial class NodeSettingsViewModel : ViewModelBase
{
    // Sentinel: "inherit / no default emulator".
    // We avoid null-suppression on Id by using an empty string.
    private const string InheritEmulatorId = "";

    private readonly MediaNode _node;
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private readonly AppSettings _settings;

    /// <summary>
    /// Central file management service used to import/copy node artwork
    /// into the portable library structure (same conventions as media items).
    /// </summary>
    private readonly FileManagementService _fileService;

    /// <summary>
    /// Logical path from the root node down to this node.
    /// Used by FileManagementService to determine the target folder
    /// for node-level assets (e.g. ["Games", "SNES"]).
    /// </summary>
    private readonly List<string> _nodePath;
    
    public ObservableCollection<SystemThemeOption> AvailableSystemThemes { get; } = new();
    
    [ObservableProperty]
    private SystemThemeOption? _selectedSystemTheme;
    
    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();
    public ObservableCollection<string> AvailableThemes { get; } = new();

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    [ObservableProperty] private bool? _randomizeCovers;
    [ObservableProperty] private bool? _randomizeMusic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveErrorMessage;

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveErrorMessage);

    [ObservableProperty] private EmulatorConfig? _selectedEmulator;

    [ObservableProperty] private string? _selectedTheme;

    public bool IsThemeExplicitlySelected => !string.IsNullOrWhiteSpace(SelectedTheme);

    private string? _inheritedEmulatorName;
    private string? _inheritedEmulatorSourceName;

    public bool IsEmulatorInherited => SelectedEmulator?.Id == InheritEmulatorId;

    public string InheritedEmulatorInfo
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_inheritedEmulatorName))
                return Strings.NodeSettings_InheritedEmulatorNone;

            return string.Format(
                Strings.NodeSettings_InheritedEmulatorInfoFormat,
                _inheritedEmulatorName,
                _inheritedEmulatorSourceName);
        }
    }
    
    // Node-level artwork for BigMode / System themes
    [ObservableProperty] private string? _nodeLogoPath;
    [ObservableProperty] private string? _nodeWallpaperPath;
    [ObservableProperty] private string? _nodeVideoPath;
    
    // System-theme selection is always available because it is used by SystemHost
    // when this node is selected in a parent host.
    public bool IsSystemThemeSelectionEnabled => true;
    
    partial void OnSelectedThemeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsThemeExplicitlySelected));
        OnPropertyChanged(nameof(IsSystemThemeSelectionEnabled));
    }

    partial void OnSelectedEmulatorChanged(EmulatorConfig? value)
    {
        OnPropertyChanged(nameof(IsEmulatorInherited));
        OnPropertyChanged(nameof(InheritedEmulatorInfo));
    }

    
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearThemeCommand { get; }

    /// <summary>
    /// Signals the view to close (true = saved, false = cancelled).
    /// </summary>
    public event Action<bool>? RequestClose;

    public enum WrapperMode
    {
        Inherit,
        None,
        Override
    }

    public sealed partial class LaunchWrapperRow : ObservableObject
    {
        [ObservableProperty] private string _path = string.Empty;
        [ObservableProperty] private string _args = string.Empty;

        public LaunchWrapperRow()
        {
        }

        public LaunchWrapperRow(LaunchWrapper wrapper)
        {
            Path = wrapper.Path ?? string.Empty;
            Args = wrapper.Args ?? string.Empty;
        }

        public LaunchWrapper ToModel()
            => new LaunchWrapper { Path = Path?.Trim() ?? string.Empty, Args = string.IsNullOrWhiteSpace(Args) ? null : Args };
    }

    [ObservableProperty]
    private WrapperMode _nativeWrapperMode = WrapperMode.Inherit;

    public ObservableCollection<LaunchWrapperRow> NativeWrappers { get; } = new();

    public IRelayCommand AddNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperDownCommand { get; }

    public NodeSettingsViewModel(
        MediaNode node,
        ObservableCollection<MediaNode> rootNodes,
        AppSettings settings,
        FileManagementService fileService,
        List<string> nodePath)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _rootNodes = rootNodes ?? throw new ArgumentNullException(nameof(rootNodes));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _nodePath = nodePath ?? throw new ArgumentNullException(nameof(nodePath));

        InitializeFromNode();
        InitializeEmulators();
        ResolveInheritedEmulatorInfo();
        LoadAvailableThemes();
        LoadSystemThemes();
        InitializeSystemThemeSelection();

        SelectedTheme = string.IsNullOrWhiteSpace(_node.ThemePath) ? null : _node.ThemePath;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        ClearThemeCommand = new RelayCommand(() => SelectedTheme = null);

        AddNativeWrapperCommand = new RelayCommand(AddNativeWrapper);
        RemoveNativeWrapperCommand = new RelayCommand<LaunchWrapperRow?>(RemoveNativeWrapper);
        MoveNativeWrapperUpCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperUp,
            row => row != null && NativeWrappers.IndexOf(row) > 0);

        MoveNativeWrapperDownCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperDown,
            row => row != null && NativeWrappers.IndexOf(row) >= 0 && NativeWrappers.IndexOf(row) < NativeWrappers.Count - 1);

        NativeWrappers.CollectionChanged += (_, __) =>
        {
            MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
            MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        };

        InitializeNativeWrapperUiFromNode();
        
        // Update once after initialization
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Imports a node-level asset (Logo/Wallpaper/Video) into the library using the same
    /// naming and folder conventions as media item assets.
    /// Returns the DataRoot-relative path to be stored on the node, or null on failure.
    /// </summary>
    public async Task<string?> ImportNodeAssetAsync(string sourceFilePath, AssetType type)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
            return null;

        var asset = await _fileService.ImportAssetAsync(sourceFilePath, _node, _nodePath, type);
        return asset?.RelativePath;
    }
    
    private void LoadSystemThemes()
    {
        AvailableSystemThemes.Clear();
        foreach (var opt in SystemThemeDiscovery.GetAvailableSystemThemes())
        {
            AvailableSystemThemes.Add(opt);
        }
    }

    private void InitializeSystemThemeSelection()
    {
        var id = _node.SystemPreviewThemeId;

        // If nothing is set -> select Default
        if (string.IsNullOrWhiteSpace(id))
        {
            SelectedSystemTheme = AvailableSystemThemes
                .FirstOrDefault(o => string.Equals(o.Id, "Default", StringComparison.OrdinalIgnoreCase));
            return;
        }

        // Search for the appropriate entry, otherwise default
        SelectedSystemTheme =
            AvailableSystemThemes.FirstOrDefault(o =>
                string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? AvailableSystemThemes.FirstOrDefault(o =>
                string.Equals(o.Id, "Default", StringComparison.OrdinalIgnoreCase));
    }
    
    public void SaveToNode()
    {
        // Option: Explicitly save the default as "Default"
        _node.SystemPreviewThemeId = SelectedSystemTheme?.Id;
    }
    
    public bool IsNativeWrapperInherit
    {
        get => NativeWrapperMode == WrapperMode.Inherit;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.Inherit;
        }
    }

    public bool IsNativeWrapperNone
    {
        get => NativeWrapperMode == WrapperMode.None;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.None;
        }
    }

    public bool IsNativeWrapperOverride
    {
        get => NativeWrapperMode == WrapperMode.Override;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.Override;
        }
    }
    
    private void InitializeNativeWrapperUiFromNode()
    {
        NativeWrappers.Clear();

        if (_node.NativeWrappersOverride == null)
        {
            NativeWrapperMode = WrapperMode.Inherit;
            return;
        }

        if (_node.NativeWrappersOverride.Count == 0)
        {
            NativeWrapperMode = WrapperMode.None;
            return;
        }

        NativeWrapperMode = WrapperMode.Override;
        foreach (var w in _node.NativeWrappersOverride)
            NativeWrappers.Add(new LaunchWrapperRow(w));
    }

    // Keep RadioButtons and IsVisible in sync if NativeWrapperMode changes in code (e.g. InitializeNativeWrapperUiFromNode)
    partial void OnNativeWrapperModeChanged(WrapperMode value)
    {
        OnPropertyChanged(nameof(IsNativeWrapperInherit));
        OnPropertyChanged(nameof(IsNativeWrapperNone));
        OnPropertyChanged(nameof(IsNativeWrapperOverride));
    }
    
    private void AddNativeWrapper()
    {
        NativeWrappers.Add(new LaunchWrapperRow());
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void RemoveNativeWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;
        NativeWrappers.Remove(row);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveNativeWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx <= 0) return;
        NativeWrappers.Move(idx, idx - 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveNativeWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= NativeWrappers.Count - 1) return;
        NativeWrappers.Move(idx, idx + 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }
    
    private void InitializeFromNode()
    {
        Name = _node.Name;
        Description = _node.Description;

        RandomizeCovers = _node.RandomizeCovers;
        RandomizeMusic = _node.RandomizeMusic;
        
        // Initialize node-level artwork from existing assets (if any)
        NodeLogoPath = _node.PrimaryLogoPath;
        NodeWallpaperPath = _node.PrimaryWallpaperPath;
        NodeVideoPath = _node.PrimaryVideoPath;
    }

    private void InitializeEmulators()
    {
        AvailableEmulators.Clear();

        AvailableEmulators.Add(new EmulatorConfig
        {
            Id = InheritEmulatorId,
            Name = Strings.Profile_NoDefaultInherit
        });

        foreach (var emu in _settings.Emulators)
            AvailableEmulators.Add(emu);

        if (!string.IsNullOrWhiteSpace(_node.DefaultEmulatorId))
        {
            SelectedEmulator = AvailableEmulators.FirstOrDefault(e => e.Id == _node.DefaultEmulatorId);
        }

        SelectedEmulator ??= AvailableEmulators.FirstOrDefault();
    }

    private void ResolveInheritedEmulatorInfo()
    {
        _inheritedEmulatorName = null;
        _inheritedEmulatorSourceName = null;

        var chain = GetNodeChain(_node, _rootNodes);
        if (chain.Count <= 1)
            return;

        for (var i = chain.Count - 2; i >= 0; i--)
        {
            var parent = chain[i];
            if (string.IsNullOrWhiteSpace(parent.DefaultEmulatorId))
                continue;

            var emulator = _settings.Emulators.FirstOrDefault(e => e.Id == parent.DefaultEmulatorId);
            _inheritedEmulatorName = emulator?.Name ?? parent.DefaultEmulatorId;
            _inheritedEmulatorSourceName = parent.Name;
            break;
        }

        OnPropertyChanged(nameof(InheritedEmulatorInfo));
    }

    private static List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node == target)
                return new List<MediaNode> { node };

            var chain = GetNodeChain(target, node.Children);
            if (chain.Count > 0)
            {
                chain.Insert(0, node);
                return chain;
            }
        }

        return new List<MediaNode>();
    }

    private void LoadAvailableThemes()
    {
        AvailableThemes.Clear();

        try
        {
            var themesRoot = AppPaths.ThemesRoot;
            if (!Directory.Exists(themesRoot))
                return;

            // Example layout: Themes/Wheel/theme.axaml, Themes/Default/theme.axaml
            foreach (var dir in Directory.EnumerateDirectories(themesRoot))
            {
                var name = Path.GetFileName(dir);
                var themeFile = Path.Combine(dir, "theme.axaml");
                if (File.Exists(themeFile))
                {
                    // Store as relative path under ThemesRoot (portable, stable when moved)
                    AvailableThemes.Add($"{name}/theme.axaml");
                }
            }
        }
        catch
        {
            // Best-effort: if scanning fails (permissions, IO errors), keep the list empty.
        }
    }
    
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return;

        var oldName = _node.Name;
        var newName = Name.Trim();

        if (!TryRenameNodeFolderIfNeeded(oldName, newName))
        {
            newName = oldName;
            Name = oldName;
            SaveErrorMessage = Strings.NodeSettings_RenameFailed;
            return;
        }

        SaveErrorMessage = null;
        _node.Name = newName;
        _node.Description = Description;
        _node.RandomizeCovers = RandomizeCovers;
        _node.RandomizeMusic = RandomizeMusic;

        _node.ThemePath = string.IsNullOrWhiteSpace(SelectedTheme) ? null : SelectedTheme;

        // Persist selected system preview theme (used by the BigMode system browser).
        // Convention: Id matches the folder name under Themes/System (e.g. "Default", "C64").
        _node.SystemPreviewThemeId = SelectedSystemTheme?.Id;
        
        if (SelectedEmulator != null && !string.IsNullOrWhiteSpace(SelectedEmulator.Id) && SelectedEmulator.Id != InheritEmulatorId)
        {
            _node.DefaultEmulatorId = SelectedEmulator.Id;
        }
        else
        {
            _node.DefaultEmulatorId = null;
        }
        
        // Persist node-level artwork (Logo / Wallpaper / Video)
        UpdateNodeAsset(AssetType.Logo, NodeLogoPath);
        UpdateNodeAsset(AssetType.Wallpaper, NodeWallpaperPath);
        UpdateNodeAsset(AssetType.Video, NodeVideoPath);
        
        // persist wrapper override with tri-state semantics
        switch (NativeWrapperMode)
        {
            case WrapperMode.Inherit:
                _node.NativeWrappersOverride = null;
                break;

            case WrapperMode.None:
                _node.NativeWrappersOverride = new List<LaunchWrapper>();
                break;

            case WrapperMode.Override:
                _node.NativeWrappersOverride = NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();
                break;
        }

        RequestClose?.Invoke(true);
    }

    private bool TryRenameNodeFolderIfNeeded(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return true;

        if (_nodePath.Count == 0)
            return true;

        var oldSegments = new List<string>(_nodePath);
        var newSegments = new List<string>(_nodePath);
        newSegments[^1] = newName;

        var oldFolder = ResolveNodeFolder(oldSegments);
        var newFolder = ResolveNodeFolder(newSegments);

        if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Directory.Exists(newFolder))
            return false;

        if (!Directory.Exists(oldFolder))
            return true;

        try
        {
            var newParentDir = Path.GetDirectoryName(newFolder);
            if (!string.IsNullOrWhiteSpace(newParentDir) && !Directory.Exists(newParentDir))
                Directory.CreateDirectory(newParentDir);

            Directory.Move(oldFolder, newFolder);

            var oldRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, oldFolder);
            var newRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, newFolder);

            UpdateAssetPathsRecursive(_node, oldRelativePrefix, newRelativePrefix);

            NodeLogoPath = _node.PrimaryLogoPath;
            NodeWallpaperPath = _node.PrimaryWallpaperPath;
            NodeVideoPath = _node.PrimaryVideoPath;
        }
        catch
        {
            return false;
        }

        return true;
    }
    
    /// <summary>
    /// Updates the node's MediaAsset list for a given asset type.
    /// Removes existing assets of that type and, if a non-empty path is provided,
    /// creates/activates a new asset entry.
    /// The path is expected to be relative to the Retromind data directory.
    /// </summary>
    private void UpdateNodeAsset(AssetType type, string? relativePath)
    {
        // Remove all existing assets of this type
        var existing = _node.Assets.Where(a => a.Type == type).ToList();
        foreach (var asset in existing)
            _node.Assets.Remove(asset);

        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        // Add a new asset entry and mark it as active
        var trimmed = relativePath.Trim();
        if (trimmed.Length == 0)
            return;

        var assetModel = new MediaAsset
        {
            Type = type,
            RelativePath = trimmed
        };

        _node.Assets.Add(assetModel);
        _node.SetActiveAsset(type, trimmed);
    }

    private static string ResolveNodeFolder(List<string> nodePathSegments)
    {
        var rawPath = Path.Combine(AppPaths.LibraryRoot, Path.Combine(nodePathSegments.ToArray()));

        var sanitizedStack = nodePathSegments
            .Select(PathHelper.SanitizePathSegment)
            .ToArray();
        var sanitizedPath = Path.Combine(AppPaths.LibraryRoot, Path.Combine(sanitizedStack));

        if (string.Equals(rawPath, sanitizedPath, StringComparison.Ordinal))
            return rawPath;

        if (Directory.Exists(rawPath))
            return rawPath;

        return sanitizedPath;
    }

    private static void UpdateAssetPathsRecursive(MediaNode node, string oldPrefix, string newPrefix)
    {
        UpdateNodeAssetPaths(node, oldPrefix, newPrefix);

        foreach (var item in node.Items)
        {
            UpdateAssetPaths(item.Assets, oldPrefix, newPrefix);

            if (item.Files != null)
            {
                foreach (var file in item.Files)
                {
                    if (file.Kind != MediaFileKind.LibraryRelative)
                        continue;

                    file.Path = ReplaceRelativePrefix(file.Path, oldPrefix, newPrefix);
                }
            }

            item.ResetActiveAssets();
            item.NotifyAssetPathsChanged();
        }

        foreach (var child in node.Children)
        {
            UpdateAssetPathsRecursive(child, oldPrefix, newPrefix);
        }
    }

    private static void UpdateNodeAssetPaths(MediaNode node, string oldPrefix, string newPrefix)
    {
        var activeByType = new Dictionary<AssetType, string?>();
        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown)
                continue;

            activeByType[type] = node.GetPrimaryAssetPath(type);
        }

        UpdateAssetPaths(node.Assets, oldPrefix, newPrefix);

        foreach (var kvp in activeByType)
        {
            var activePath = kvp.Value;
            if (string.IsNullOrWhiteSpace(activePath))
                continue;

            var updated = ReplaceRelativePrefix(activePath, oldPrefix, newPrefix);
            if (!string.Equals(updated, activePath, StringComparison.OrdinalIgnoreCase))
                node.SetActiveAsset(kvp.Key, updated);
        }
    }

    private static void UpdateAssetPaths(IEnumerable<MediaAsset> assets, string oldPrefix, string newPrefix)
    {
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.RelativePath))
                continue;

            asset.RelativePath = ReplaceRelativePrefix(asset.RelativePath, oldPrefix, newPrefix);
        }
    }

    private static string ReplaceRelativePrefix(string path, string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var normalizedPath = NormalizeRelativePath(path);
        var normalizedOld = NormalizeRelativePath(oldPrefix);
        var normalizedNew = NormalizeRelativePath(newPrefix);

        if (string.Equals(normalizedPath, normalizedOld, StringComparison.OrdinalIgnoreCase))
            return normalizedNew;

        var oldWithSlash = normalizedOld.EndsWith("/", StringComparison.Ordinal) ? normalizedOld : normalizedOld + "/";
        if (normalizedPath.StartsWith(oldWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedNewWithSlash = normalizedNew.EndsWith("/", StringComparison.Ordinal)
                ? normalizedNew
                : normalizedNew + "/";
            return normalizedNewWithSlash + normalizedPath.Substring(oldWithSlash.Length);
        }

        return path;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
}
