using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    // --- Native wrapper chain (Tri-state; item-level) ---

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
        [ObservableProperty] private string _source = string.Empty;

        public LaunchWrapperRow()
        {
        }

        public LaunchWrapperRow(LaunchWrapper wrapper, string? source = null)
        {
            Path = wrapper.Path ?? string.Empty;
            Args = wrapper.Args ?? string.Empty;
            Source = source ?? string.Empty;
        }

        public LaunchWrapper ToModel()
            => new LaunchWrapper
            {
                Path = Path?.Trim() ?? string.Empty,
                Args = string.IsNullOrWhiteSpace(Args) ? null : Args
            };
    }

    [ObservableProperty]
    private WrapperMode _nativeWrapperMode = WrapperMode.Inherit;

    public ObservableCollection<LaunchWrapperRow> NativeWrappers { get; } = new();
    public ObservableCollection<LaunchWrapperRow> InheritedWrappers { get; } = new();

    private string _inheritedWrappersInfo = Strings.EditMedia_InheritedWrappersNone;

    public bool HasInheritedWrappers => InheritedWrappers.Count > 0;
    public string InheritedWrappersInfo => _inheritedWrappersInfo;

    public IRelayCommand AddNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperDownCommand { get; }

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

    private void InitializeNativeWrapperUiFromItem()
    {
        // Important: detach old rows (in case the view model instance is reused).
        foreach (var row in NativeWrappers)
            UnwireWrapperRow(row);

        NativeWrappers.Clear();

        if (_originalItem.NativeWrappersOverride == null)
        {
            NativeWrapperMode = WrapperMode.Inherit;
            return;
        }

        if (_originalItem.NativeWrappersOverride.Count == 0)
        {
            NativeWrapperMode = WrapperMode.None;
            return;
        }

        NativeWrapperMode = WrapperMode.Override;
        foreach (var w in _originalItem.NativeWrappersOverride)
        {
            var row = new LaunchWrapperRow(w, Strings.Common_SourceItem);
            WireWrapperRow(row);
            NativeWrappers.Add(row);
        }
    }

    private void WireWrapperRow(LaunchWrapperRow row)
    {
        row.PropertyChanged += OnWrapperRowPropertyChanged;
    }

    private void UnwireWrapperRow(LaunchWrapperRow row)
    {
        row.PropertyChanged -= OnWrapperRowPropertyChanged;
    }

    private void OnWrapperRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Whenever Path/Args change, update the preview (users expect it to be live).
        if (e.PropertyName == nameof(LaunchWrapperRow.Path) ||
            e.PropertyName == nameof(LaunchWrapperRow.Args))
        {
            if (sender is LaunchWrapperRow row && !string.Equals(row.Source, Strings.Common_SourceItem, StringComparison.Ordinal))
                row.Source = Strings.Common_SourceItem;

            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Attach change tracking to an environment override row so that
    /// edits to Key/Value immediately refresh the launch preview.
    /// </summary>
    private void WireEnvRow(EnvVarRow row)
    {
        row.PropertyChanged += OnEnvRowPropertyChanged;
    }

    /// <summary>
    /// Detach change tracking from an environment override row.
    /// </summary>
    private void UnwireEnvRow(EnvVarRow row)
    {
        row.PropertyChanged -= OnEnvRowPropertyChanged;
    }

    private void OnEnvRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only rebuild the preview for changes that actually affect the
        // environment prefix (Key/Value). This keeps updates lean
        if (e.PropertyName == nameof(EnvVarRow.Key) ||
            e.PropertyName == nameof(EnvVarRow.Value))
        {
            if (sender is EnvVarRow row && row.IsInherited)
            {
                row.IsInherited = false;
                row.Source = Strings.Common_SourceItem;
            }

            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private void InitializeEnvironmentOverridesFromItem()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_originalItem.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in _originalItem.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                overrides[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        RebuildEnvironmentOverridesFromInheritance(overrides);
    }

    private Dictionary<string, string> CaptureCurrentEnvironmentOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            overrides[row.Key.Trim()] = row.Value ?? string.Empty;
        }

        return overrides;
    }

    private void RebuildEnvironmentOverridesFromInheritance(Dictionary<string, string> itemOverrides)
    {
        var (inheritedEnv, inheritedSources) = ResolveInheritedEnvironmentOverridesWithSources();

        EnvironmentOverrides.Clear();
        var rowsByKey = new Dictionary<string, EnvVarRow>(StringComparer.Ordinal);

        foreach (var kv in inheritedEnv)
        {
            var source = inheritedSources.TryGetValue(kv.Key, out var label) ? label : string.Empty;
            var row = new EnvVarRow
            {
                Key = kv.Key,
                Value = kv.Value,
                IsInherited = true,
                Source = source
            };

            rowsByKey[kv.Key] = row;
        }

        foreach (var kv in itemOverrides)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            var key = kv.Key.Trim();
            var value = kv.Value ?? string.Empty;

            if (rowsByKey.TryGetValue(key, out var row))
            {
                row.Value = value;
                row.IsInherited = false;
                row.Source = Strings.Common_SourceItem;
            }
            else
            {
                row = new EnvVarRow
                {
                    Key = key,
                    Value = value,
                    IsInherited = false,
                    Source = Strings.Common_SourceItem
                };
                rowsByKey[key] = row;
            }
        }

        foreach (var row in rowsByKey.Values.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            EnvironmentOverrides.Add(row);
    }

    private (Dictionary<string, string> Env, Dictionary<string, string> Sources) ResolveInheritedEnvironmentOverridesWithSources()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        EmulatorConfig? effectiveEmulator = null;
        if (MediaType == MediaType.Emulator)
            effectiveEmulator = ResolveSelectedEmulatorConfig();

        if (effectiveEmulator?.EnvironmentOverrides is { Count: > 0 })
        {
            var emulatorName = string.IsNullOrWhiteSpace(effectiveEmulator.Name)
                ? effectiveEmulator.Id
                : effectiveEmulator.Name;

            foreach (var kv in effectiveEmulator.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                var key = kv.Key.Trim();
                env[key] = kv.Value ?? string.Empty;
                sources[key] = string.Format(Strings.Common_SourceEmulatorFormat, emulatorName);
            }
        }

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = PathHelper.GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            foreach (var node in chain)
            {
                if (node.EnvironmentOverrides == null)
                    continue;

                if (node.EnvironmentOverrides.Count > 0)
                {
                    foreach (var kv in node.EnvironmentOverrides)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            continue;

                        var key = kv.Key.Trim();
                        env[key] = kv.Value ?? string.Empty;
                        sources[key] = string.Format(Strings.Common_SourceNodeFormat, node.Name);
                    }
                }

                break;
            }
        }

        return (env, sources);
    }

    partial void OnNativeWrapperModeChanged(WrapperMode value)
    {
        OnPropertyChanged(nameof(IsNativeWrapperInherit));
        OnPropertyChanged(nameof(IsNativeWrapperNone));
        OnPropertyChanged(nameof(IsNativeWrapperOverride));
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();

        if (value == WrapperMode.Override &&
            NativeWrappers.Count == 0 &&
            InheritedWrappers.Count > 0)
        {
            foreach (var wrapper in InheritedWrappers)
            {
                var row = new LaunchWrapperRow(wrapper.ToModel(), wrapper.Source);
                WireWrapperRow(row);
                NativeWrappers.Add(row);
            }
        }
    }

    private void AddNativeWrapper()
    {
        var row = new LaunchWrapperRow();
        row.Source = Strings.Common_SourceItem;
        WireWrapperRow(row);
        NativeWrappers.Add(row);

        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void RemoveNativeWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;

        UnwireWrapperRow(row);
        NativeWrappers.Remove(row);

        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void MoveNativeWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx <= 0) return;
        NativeWrappers.Move(idx, idx - 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void MoveNativeWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= NativeWrappers.Count - 1) return;
        NativeWrappers.Move(idx, idx + 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }
}
