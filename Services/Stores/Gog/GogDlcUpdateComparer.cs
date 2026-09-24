using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

public enum GogDlcUpdateState
{
    Unknown,
    UpToDate,
    UpdateAvailable
}

public sealed record GogDlcReapplyTarget(
    GogDlcInstallationState Installed,
    GogDlcCatalogItem CatalogItem);

public sealed record GogDlcReapplyPlan(
    IReadOnlyList<GogDlcReapplyTarget> Targets,
    IReadOnlyList<GogDlcInstallationState> Unavailable);

public static class GogDlcUpdateComparer
{
    public const string CatalogSignaturePrefix = "catalog-v1:";

    public static GogDlcUpdateState Evaluate(
        GogDlcInstallationState installed,
        GogDlcInstallerMetadata? remote)
    {
        if (remote == null)
            return GogDlcUpdateState.Unknown;

        var installedVersion = Normalize(installed.InstalledVersion);
        var remoteVersion = Normalize(remote.Version);
        if (installedVersion.Length > 0 && remoteVersion.Length > 0)
        {
            if (!string.Equals(installedVersion, remoteVersion, StringComparison.OrdinalIgnoreCase))
                return GogDlcUpdateState.UpdateAvailable;

            if (!IsCatalogSignature(installed.InstalledInstallerSignature) ||
                string.IsNullOrWhiteSpace(remote.Signature))
            {
                return GogDlcUpdateState.UpToDate;
            }

            return SignaturesEqual(installed.InstalledInstallerSignature!, remote.Signature)
                ? GogDlcUpdateState.UpToDate
                : GogDlcUpdateState.UpdateAvailable;
        }

        if (IsCatalogSignature(installed.InstalledInstallerSignature) &&
            !string.IsNullOrWhiteSpace(remote.Signature))
        {
            return SignaturesEqual(installed.InstalledInstallerSignature!, remote.Signature)
                ? GogDlcUpdateState.UpToDate
                : GogDlcUpdateState.UpdateAvailable;
        }

        return GogDlcUpdateState.Unknown;
    }

    public static bool HasUpdate(
        IEnumerable<GogDlcInstallationState>? installedDlcs,
        IEnumerable<GogDlcCatalogItem> catalog,
        GogInstallPlatform platform)
    {
        if (installedDlcs == null)
            return false;

        var catalogById = catalog.ToDictionary(static item => item.ProductId, StringComparer.Ordinal);
        return installedDlcs.Any(installed =>
            catalogById.TryGetValue(installed.ProductId, out var remote) &&
            Evaluate(installed, remote.GetInstallerMetadata(platform)) == GogDlcUpdateState.UpdateAvailable);
    }

    /// <summary>
    /// Builds the set of already installed DLCs that must be reapplied after a
    /// GOG offline installation of the main game. Unowned DLCs are never
    /// added; installed DLCs without a current installer for the selected
    /// platform are reported separately.
    /// </summary>
    public static GogDlcReapplyPlan CreateReapplyPlan(
        IEnumerable<GogDlcInstallationState>? installedDlcs,
        IEnumerable<GogDlcCatalogItem> catalog,
        GogInstallPlatform platform)
    {
        if (installedDlcs == null)
            return new GogDlcReapplyPlan([], []);

        var catalogById = catalog
            .Where(static item => !string.IsNullOrWhiteSpace(item.ProductId))
            .GroupBy(static item => item.ProductId.Trim(), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var targets = new List<GogDlcReapplyTarget>();
        var unavailable = new List<GogDlcInstallationState>();
        var seenProductIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var installed in installedDlcs)
        {
            var productId = installed.ProductId?.Trim();
            if (string.IsNullOrWhiteSpace(productId) || !seenProductIds.Add(productId))
                continue;

            if (catalogById.TryGetValue(productId, out var remote) &&
                remote.AvailableInstallerPlatforms.Contains(platform))
            {
                targets.Add(new GogDlcReapplyTarget(installed, remote));
            }
            else
            {
                unavailable.Add(installed);
            }
        }

        return new GogDlcReapplyPlan(targets, unavailable);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsCatalogSignature(string? signature) =>
        !string.IsNullOrWhiteSpace(signature) &&
        signature.StartsWith(CatalogSignaturePrefix, StringComparison.Ordinal);

    private static bool SignaturesEqual(string installed, string remote) =>
        string.Equals(installed.Trim(), remote.Trim(), StringComparison.OrdinalIgnoreCase);
}
