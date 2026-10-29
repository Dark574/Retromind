using System.Collections.Generic;

namespace Retromind.ViewModels;

public sealed record HelpWorkflowGuide(string Title, string Description, string Details);

public sealed class HelpGuidesViewModel : ViewModelBase
{
    public string WindowTitle => T("Help.Window.Title", "Retromind Help");
    public string Title => T("Help.Workflows.Title", "Common workflows");
    public string Intro => T(
        "Help.Workflows.Intro",
        "Short guides for common tasks in Retromind.");
    public string OpenText => T("Help.Workflows.Open", "Open workflow guides...");
    public string CloseText => T("Button_Close", "Close");

    public IReadOnlyList<HelpWorkflowGuide> Guides { get; } =
    [
        new(
            T("Help.Workflow.Nodes.Title", "Organize categories and nodes"),
            T("Help.Workflow.Nodes.Description", "Build an inheritable library structure without configuring every item separately."),
            T(
                "Help.Workflow.Nodes.Details",
                "• Create parent categories for broad areas such as Games, Movies or Books and child categories for systems or collections.\n• Set Game system for game identification and Default emulator (for this folder) on the category where they should begin to apply; child nodes and their items inherit them.\n• Use Advanced launch settings at node level when they should apply to every child. Item settings take precedence over node settings and can therefore be used for specific exceptions.\n• Enable Apply default emulator to existing items when existing items should adopt a changed default emulator too.\n• BigMode themes can be assigned per category and do not follow the launch-configuration inheritance rules.")),
        new(
            T("Help.Workflow.Emulator.Title", "Create and assign an emulator profile"),
            T("Help.Workflow.Emulator.Description", "Reuse one tested launch configuration for a system or category."),
            T(
                "Help.Workflow.Emulator.Details",
                "• Open Emulators, add a profile, and select its executable.\n• Enter the emulator arguments and use {file} where the selected game or medium belongs.\n• Test the profile on one item before assigning it as a category's default emulator.\n• Put settings shared by many games on the emulator or node; use item overrides only for exceptions.\n• Wrapper order is significant. Keep wrappers such as gamemoderun or mangohud in the order in which they should wrap the final command.")),
        new(
            T("Help.Workflow.RomBulk.Title", "Bulk-import ROMs and find metadata"),
            T("Help.Workflow.RomBulk.Description", "Import a ROM folder into a prepared category, then add metadata and media."),
            T(
                "Help.Workflow.RomBulk.Details",
                "• Right-click the target category in the library tree and choose Settings / Rename... Set Game system for game identification and, under Launch options, Default emulator (for this folder) so imported ROMs inherit the correct system and launch configuration.\n• Under Settings → Metadata → Providers, configure at least one suitable scraper. Under Import, choose which metadata and media types to import and how existing data should be handled.\n• Right-click the target category in the library tree and choose Import → Local folder (ROMs)...\n• Select the ROM folder and confirm or edit the comma-separated list of file extensions. Retromind also searches subfolders, skips track files referenced by CUE files and groups multi-disc games recognized from their filenames.\n• ROM imports are additive. You can run the same folder import on the category again later; Retromind only adds ROMs whose launch path is not already present and does not replace or delete existing items.\n• After importing, right-click the target category again and choose Search metadata (all)... Select the source in the dialog and then Launch. The search includes the current category and its child categories.\n• Ambiguous results are not applied automatically during bulk scraping. Use Search metadata... on the affected item to choose the correct result manually.")),
        new(
            T("Help.Workflow.Windows.Title", "Install a Windows game manually"),
            T("Help.Workflow.Windows.Description", "Run an installer with system Wine or UMU/Proton while keeping its prefix portable."),
            T(
                "Help.Workflow.Windows.Details",
                "• Register the required Wine or Proton version under Settings → Launch. Then create a suitable Wine or UMU profile under Settings → Emulators, or select an existing one. Always use an UMU profile for Proton.\n• Enable Use per-game prefix (WINEPREFIX) on the emulator and use {file} as its launch argument.\n• Select that emulator profile on the item and start the installer through Test launch (without tracking). The corresponding prefix is created automatically on first launch.\n• In the Windows installer, Library/Games is normally available as R:. If that letter is occupied, Retromind chooses another free drive.\n• A .exe launched as Native through system Wine uses system Wine's default prefix. Retromind's runner, dedicated prefix and portable games drive are not applied.\n• Retromind stores automatically created prefixes under Library/Prefixes by default. The exact path is shown on the item under Launch options as PrefixPath (relative to Library/). Removing an item keeps its prefix; enter this path on the new item to reuse it.")),
        new(
            T("Help.Workflow.Stores.Title", "Import and manage store games"),
            T("Help.Workflow.Stores.Description", "Import Steam, GOG and Epic games and manage store links and installations."),
            T(
                "Help.Workflow.Stores.Details",
                "• Bulk import: Right-click the target category in the library tree and choose Import. Import Steam games... adds all locally detected Steam installations after confirmation. With Import GOG games... and Import Epic games..., select several titles, optionally use Select all filtered, then choose Add selection.\n• Store imports are additive. Run the bulk import on the same category again later to add newly available games. Store games already present there are not created again, replaced or deleted.\n• Single import: Open the same import dialog for GOG or Epic, select only one title and choose Add selection. The Steam importer currently has no single-selection picker and imports all detected Steam games that are not already present in the target category.\n• Link an existing item: Right-click the item and choose Store → Link to GOG game... or Link to Epic Games title... Select the title from your store library and confirm with Link game. Existing files, launch settings, media and metadata are retained.\n• Switch stores: On the item, choose Store → Switch to GOG... or Switch to Epic Games... A Retromind-managed installation must first be removed through Store → Uninstall; the library item remains.\n• Install: For an imported or linked title that is not installed, choose Store → Install. For Windows games, select the desired Wine/Proton runner in the installation dialog.\n• Updates and repair: Use Store → Check for GOG updates or Check for Epic Games updates. When an update is available, Update appears. GOG also provides Reinstall / Switch Version; Epic provides Repair installation.\n• Uninstall: Store → Uninstall removes the Retromind-managed installation, not the library item. Shared prefixes and files that cannot be clearly attributed to the installation are retained.")),
        new(
            T("Help.Workflow.Metadata.Title", "Find metadata and artwork"),
            T("Help.Workflow.Metadata.Description", "Improve matching results and control which discovered data is adopted."),
            T(
                "Help.Workflow.Metadata.Details",
                "• Assign the correct game system to the category before searching; items inherit it and may override it individually.\n• Under Settings → Metadata → Providers, configure the available scrapers. Under Import, choose which metadata and media types to copy.\n• Use the item search to review one result manually, or the category's bulk search for many items.\n• File hashes can identify supported ROMs more reliably than names when the provider and system support them.\n• Existing media is preserved or supplemented according to Default for existing media.")),
        new(
            T("Help.Workflow.MultiDisc.Title", "Configure multi-disc games"),
            T("Help.Workflow.MultiDisc.Description", "Keep all discs on one item and launch them in a predictable order."),
            T(
                "Help.Workflow.MultiDisc.Details",
                "• Right-click the desired category in the library tree and choose Add Media.\n• Select all disc files for the same game at once in the file dialog. Then confirm Combine them into one multi-disc item? and enter a shared title.\n• Retromind recognizes labels such as Disc, Disk or CD with their number in filenames and sorts the files accordingly. Without recognizable numbering, it sorts by filename, so name the files clearly before adding them.\n• Enable Use playlist (.m3u) for multi-disc items in the emulator profile when the emulator supports .m3u. Retromind creates a portable playlist at launch and passes it through {file}.\n• Without playlist mode, Disc 1 is used for the normal launch.\n• Select all discs together when creating the item. Change under Launch options only replaces the primary launch file of an existing item.\n• Keep all disc paths inside the Retromind data location when the setup should remain portable.")),
        new(
            T("Help.Workflow.Portable.Title", "Move a portable Retromind installation"),
            T("Help.Workflow.Portable.Description", "Relocate the data directory without retaining dependencies on its old location."),
            T(
                "Help.Workflow.Portable.Details",
                "• Under Settings → Launch, enable Store launch file paths relative to Retromind data folder (portable) before saving launch settings that point inside the data directory.\n• Close Retromind and copy the complete portable data structure, including Library, Emulators, Tools and portable HOME data when used.\n• Paths inside the data root are stored relatively where supported; intentionally external paths remain absolute.\n• Retromind repairs managed Proton-prefix links and portable games-drive mappings when they still point to the previous location.\n• Test one native title, one emulator and one Wine/Proton title before removing the original copy.")),
        new(
            T("Help.Workflow.Backup.Title", "Back up and restore the library"),
            T("Help.Workflow.Backup.Description", "Protect settings and library metadata while understanding what is outside the backup."),
            T(
                "Help.Workflow.Backup.Details",
                "• Under Settings → Data, enable Create automatic metadata backups, choose the desired triggers and use Manage backups... for inspection or restore.\n• A metadata backup contains the library database and settings. It does not contain games, artwork, videos, themes, prefixes or portable HOME data.\n• Back up the complete Retromind data directory separately when those files must be protected too.\n• Retromind closes after a successful restore so the restored state is loaded cleanly on the next start.")),
        new(
            T("Help.Workflow.Diagnostics.Title", "Investigate launch problems"),
            T("Help.Workflow.Diagnostics.Description", "Collect the useful evidence before changing a working configuration."),
            T(
                "Help.Workflow.Diagnostics.Details",
                "• Use Test launch (without tracking) so failed attempts do not alter play statistics.\n• Check the process window and the saved launch log for the executable, arguments, working directory, runner, prefix and exit code.\n• Compare inherited emulator and node values with item overrides, especially wrappers, environment variables and working directories.\n• Verify that the selected media type and emulator match the file: a Native .exe on Linux does not automatically use a Retromind Wine/Proton profile.\n• Copy Support information from Help when requesting assistance, and review launch logs before sharing them."))
    ];
}
