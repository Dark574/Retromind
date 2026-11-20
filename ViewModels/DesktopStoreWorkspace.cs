using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Models;

namespace Retromind.ViewModels;

// The two stores share the desktop library and the same operation guard as game launches.
internal sealed record DesktopStoreWorkspace(
    AppSettings Settings,
    Func<Window?> GetWindow,
    Func<IStorageProvider?> GetStorageProvider,
    Func<ObservableCollection<MediaNode>> GetRootItems,
    Func<bool> GetMultiSelectionActive,
    Func<bool> GetOperationInProgress,
    Action<bool> SetOperationInProgress,
    Func<MediaItem, MediaNode?> FindParentNode,
    Func<MediaItem, EmulatorConfig?> FindInheritedEmulator,
    Func<Task<bool>> SaveLibraryAsync,
    Action MarkLibraryDirty,
    Action MarkLibraryDirtyAndSaveSoon,
    Action RefreshActions,
    Func<MediaNode, Task> RefreshItemAsync,
    Func<MediaNode, IReadOnlyList<MediaItem>, Task> PublishImportAsync);
