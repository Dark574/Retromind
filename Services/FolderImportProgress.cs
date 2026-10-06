namespace Retromind.Services;

public enum FolderImportStage
{
    ScanningFiles,
    ReadingCueFiles,
    PreparingItems,
    MatchingAssets
}

public readonly record struct FolderImportProgress(
    FolderImportStage Stage,
    long FilesScanned = 0,
    int MatchingFileCount = 0,
    int CompletedCount = 0,
    int TotalCount = 0);
