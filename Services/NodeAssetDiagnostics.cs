using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

internal static class NodeAssetDiagnostics
{
    public static void Report(IErrorService errors, NodeAssetOperationResult result, string operation, string nodeId)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var context = new Dictionary<string, string>
        {
            ["Operation"] = operation, ["NodeId"] = nodeId,
            ["SourcePrefix"] = result.SourcePrefix, ["TargetPrefix"] = result.TargetPrefix,
            ["RollbackComplete"] = result.RollbackComplete.ToString(),
            ["MetadataRecoveryRequired"] = result.MetadataRecoveryRequired.ToString(),
            ["RecoveryFileCount"] = result.RecoveryFiles.Count.ToString(CultureInfo.InvariantCulture)
        };
        if (result.FailureType != null) context["FailureType"] = result.FailureType;
        if (result.FailureReason != null) context["FailureReason"] = result.FailureReason;
        if (result.FailurePhase != null) context["FailurePhase"] = result.FailurePhase;
        if (result.StagingDirectory != null) context["StagingDirectory"] = result.StagingDirectory;
        if (result.RecoveryManifest != null) context["RecoveryManifest"] = result.RecoveryManifest;
        context["RecoveryLocations"] = string.Join("\n", result.RecoveryFiles.Take(8)
            .Select(file => $"{file.OriginalPath} <- {file.RetainedPath}"));

        void Emit(string code, ErrorSeverity severity, string message)
        {
            try { errors.Report(new ErrorReport(code, severity, nameof(NodeAssetFolderHelper), message,
                CorrelationId: correlation, Context: context)); }
            catch { /* Diagnostics must not alter recovery or the result returned to either caller. */ }
        }

        if (result.FailureCode != null)
            Emit(result.FailureCode, ErrorSeverity.Error, "The category asset operation failed; the category change was aborted.");
        if (!result.RollbackComplete)
            Emit("NodeAssets.RollbackIncomplete", ErrorSeverity.Critical,
                "Category asset recovery is incomplete. Retained files must be preserved for manual recovery.");
        if (result.CleanupFailures.Count > 0)
        {
            context["CleanupFailureCount"] = result.CleanupFailures.Count.ToString(CultureInfo.InvariantCulture);
            context["CleanupLocations"] = string.Join("\n", result.CleanupFailures.Take(8));
            Emit("NodeAssets.CleanupFailed", ErrorSeverity.Diagnostic,
                "Best-effort category asset cleanup or recovery-manifest writing failed.");
        }
    }
}
