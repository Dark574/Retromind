using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task<bool> ConfirmGogInstallSpaceAsync(
        Window owner,
        GogInstallerPackage package,
        string stagingDirectory,
        string installPath,
        bool useTemporaryLinuxDestination)
    {
        var assessment = GogInstallSpaceService.Assess(package, stagingDirectory, installPath);
        switch (assessment.Status)
        {
            case GogInstallSpaceStatus.InsufficientForDownload:
                await ShowInfoDialog(
                    owner,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Gog.Install.SpaceDownloadInsufficientFormat",
                            "There is not enough free space for the GOG offline installer download.\n\nRemaining download: {0}\nAvailable: {1}\nChecked filesystem: {2}\n\nRetromind keeps an additional 1 GB free as a safety reserve."),
                        GogInstallSpaceService.FormatByteSize(assessment.RemainingDownloadBytes.GetValueOrDefault()),
                        GogInstallSpaceService.FormatByteSize(assessment.AvailableBytes.GetValueOrDefault()),
                        assessment.CheckedPath));
                return false;

            case GogInstallSpaceStatus.EstimatedInstallMayNotFit:
                if (!await ShowConfirmDialog(
                        owner,
                        string.Format(
                            CultureInfo.CurrentCulture,
                            T(
                                "Gog.Install.SpaceEstimateWarningFormat",
                                "The installer download fits, but the complete installation may need more free space.\n\nEstimated additional peak usage: at least {0}\nAvailable: {1}\nChecked filesystem: {2}\n\nGOG does not provide a reliable installed size. Continue anyway?"),
                            GogInstallSpaceService.FormatByteSize(assessment.EstimatedPeakAdditionalBytes.GetValueOrDefault()),
                            GogInstallSpaceService.FormatByteSize(assessment.AvailableBytes.GetValueOrDefault()),
                            assessment.CheckedPath)))
                {
                    return false;
                }

                break;

            case GogInstallSpaceStatus.PackageSizeUnknown:
                if (!await ShowConfirmDialog(
                        owner,
                        string.Format(
                            CultureInfo.CurrentCulture,
                            T(
                                "Gog.Install.SpaceUnknownWarningFormat",
                                "GOG did not provide a complete installer size. Retromind cannot reliably check whether the download and installation fit on the selected filesystem:\n{0}\n\nIf the server supplies the remaining download size later, the download will still stop before using the safety reserve. Continue?"),
                            assessment.CheckedPath)))
                {
                    return false;
                }

                break;

            case GogInstallSpaceStatus.FreeSpaceUnavailable:
                if (!await ShowConfirmDialog(
                        owner,
                        string.Format(
                            CultureInfo.CurrentCulture,
                            T(
                                "Gog.Install.SpaceUnavailableWarningFormat",
                                "Retromind could not determine the available free space for:\n{0}\n\nContinue without a complete storage check?"),
                            assessment.CheckedPath)))
                {
                    return false;
                }

                break;
        }

        if (useTemporaryLinuxDestination &&
            OperatingSystem.IsLinux() &&
            GogInstallerExecutionService.HasShellSensitivePathCharacters(installPath) &&
            assessment.InstallerBytes is > 0 &&
            GogInstallSpaceService.TryGetAvailableBytes(Path.GetTempPath(), out var temporaryAvailableBytes))
        {
            var temporaryEstimate = assessment.InstallerBytes.GetValueOrDefault() >
                                    long.MaxValue - GogInstallSpaceService.FreeSpaceReserveBytes
                ? long.MaxValue
                : assessment.InstallerBytes.GetValueOrDefault() + GogInstallSpaceService.FreeSpaceReserveBytes;
            if (temporaryAvailableBytes < temporaryEstimate)
            {
                return await ShowConfirmDialog(
                    owner,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Gog.Install.SpaceTemporaryWarningFormat",
                            "Because the Linux install path contains shell-sensitive characters, the installer must first use temporary storage. It may need at least {0}, but only {1} is available in:\n{2}\n\nContinue anyway?"),
                        GogInstallSpaceService.FormatByteSize(temporaryEstimate),
                        GogInstallSpaceService.FormatByteSize(temporaryAvailableBytes),
                        Path.GetTempPath()));
            }
        }

        return true;
    }
}
