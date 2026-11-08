using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Helpers;

/// <summary>Category-asset operation outcome. All recovery locations are relative to DataRoot.</summary>
public sealed class NodeAssetOperationResult
{
    public bool Success { get; internal set; }
    public string? FailureCode { get; internal set; }
    public string? FailureType { get; internal set; }
    public string? FailureReason { get; internal set; }
    public string? FailurePhase { get; internal set; }
    public bool RollbackComplete { get; internal set; } = true;
    public bool MetadataRecoveryRequired { get; internal set; }
    public string SourcePrefix { get; internal set; } = "";
    public string TargetPrefix { get; internal set; } = "";
    public string? StagingDirectory { get; internal set; }
    public string? RecoveryManifest { get; internal set; }
    public List<NodeAssetRecoveryFile> RecoveryFiles { get; } = new();
    public List<string> CleanupFailures { get; } = new();

    internal void Fail(string code, Exception? exception = null)
    {
        Success = false;
        FailureCode = code;
        FailureType = exception?.GetType().Name;
    }
}

public sealed record NodeAssetRecoveryFile(string OriginalPath, string RetainedPath);

// Explicit per-operation IO boundary for deterministic failure tests; never a global service/provider.
internal sealed class NodeAssetFileOperations
{
    public Action<string, string> MoveFile { get; init; } = (source, target) => File.Move(source, target);
    public Action<string> DeleteDirectory { get; init; } = path => Directory.Delete(path);
    public Action? AfterFilesMoved { get; init; }
}
