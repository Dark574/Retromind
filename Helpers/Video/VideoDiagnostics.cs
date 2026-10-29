using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Helpers.Video;

/// <summary>Output helpers only; playback decisions remain with BigMode and the surfaces.</summary>
internal static class VideoDiagnostics
{
    internal static ErrorReport CreateReport(
        string component, string code, string operation, int surfaceIndex,
        string? videoPath = null, Exception? exception = null,
        IReadOnlyDictionary<string, string>? details = null)
    {
        var context = new Dictionary<string, string>
        {
            ["Operation"] = operation,
            ["SurfaceIndex"] = surfaceIndex.ToString(CultureInfo.InvariantCulture)
        };
        if (videoPath != null)
            context["VideoPath"] = videoPath;
        if (details != null)
            foreach (var pair in details)
                context[pair.Key] = pair.Value;

        var message = code switch
        {
            "Video.PlaybackFailed" => "Video playback could not be completed.",
            "Video.StopFailed" => "The video player could not be stopped cleanly.",
            _ => "Video resources could not be cleaned up completely."
        };
        return new ErrorReport(code,
            code == "Video.PlaybackFailed" ? ErrorSeverity.Warning : ErrorSeverity.Diagnostic,
            component, message, exception, Context: context);
    }

    internal static async Task ReportAsync(IErrorService errorService, ErrorReport report)
    {
        try
        {
            // The central writer is synchronous. Never run it on a VLC callback or
            // the UI thread, or hold a media/buffer lock while it writes to disk.
            await Task.Run(() => errorService.Report(report)).ConfigureAwait(false);
        }
        catch
        {
            // Output failures must not become unobserved tasks or affect native cleanup.
        }
    }
}
