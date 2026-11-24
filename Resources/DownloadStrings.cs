namespace Retromind.Resources;

public static class DownloadStrings
{
    private static string Get(string key, string fallback) =>
        Strings.ResourceManager.GetString("Downloads." + key, Strings.Culture) ?? fallback;

    public static string Title => Get("Title", "Download manager");
    public static string AddUpdate => Get("AddUpdate", "Queue update");
    public static string AddWindows => Get("AddWindows", "Queue GOG installer (Windows)");
    public static string AddLinux => Get("AddLinux", "Queue GOG installer (Linux)");
    public static string Exclude => Get("Exclude", "Exclude from automatic updates");
    public static string Empty => Get("Empty", "The download queue is empty.");
    public static string Unavailable => Get("Unavailable", "The download queue could not be accessed. Restart Retromind after checking the State directory.");
    public static string Start => Get("Start", "Start now");
    public static string Pause => Get("Pause", "Pause");
    public static string Resume => Get("Resume", "Resume");
    public static string PauseQueue => Get("PauseQueue", "Pause all downloads");
    public static string ResumeQueue => Get("ResumeQueue", "Resume download queue");
    public static string QueuePauseHint => Get("QueuePauseHint", "Other queued downloads stay paused. You can manually start prepared GOG installations, including required DLC downloads. Epic updates and repairs require resuming the queue. Installations already in progress finish.");
    public static string Cancel => Get("Cancel", "Cancel");
    public static string Retry => Get("Retry", "Retry");
    public static string Install => Get("Install", "Install");
    public static string InstallationReadyBadge => Get("InstallationReadyBadge", "Downloaded and ready to install in the download manager");
    public static string Repair => Get("Repair", "Repair");
    public static string Earlier => Get("Earlier", "Move up");
    public static string Later => Get("Later", "Move down");
    public static string Remove => Get("Remove", "Remove from list");
    public static string ExitBusy => Get("ExitBusy", "A store installation or maintenance operation is in progress. Let it finish before closing Retromind or restoring a backup. You can leave Retromind open and close it safely when the operation ends.");
    public static string Mode => Get("Mode", "Automatic game updates");
    public static string Window => Get("Window", "Only download within a daily time window");
    public static string WindowFrom => Get("WindowFrom", "Start (local time)");
    public static string WindowTo => Get("WindowTo", "End (local time)");
    public static string WindowHint => Get("WindowHint", "The window may cross midnight. Equal times allow downloads all day. Start now overrides the window for one job.");
    public static string Speed => Get("Speed", "Download speed in MB/s (0 = unlimited)");
    public static string EpicHint => Get("EpicHint", "Epic uses Legendary to write game files as it downloads. In download-only mode, Epic updates wait for manual approval. A combined update runs to completion. Legendary has no reliable speed limit in MB/s.");
    public static string GogHint => Get("GogHint", "GOG offline installers can require interaction. Downloaded GOG updates wait for manual installation.");
    public static string Archive => Get("Archive", "GOG offline installer archive");
    public static string ArchiveHintFormat => Get("ArchiveHintFormat", "Leave this field blank to use {0} as the archive folder.");
    public static string Browse => Get("Browse", "Browse…");
    public static string KeepInstallers => Get("KeepInstallers", "Keep offline installers after installation");
}
