using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Helpers;

public static partial class NodeAssetFolderHelper
{
    private static TargetFolderReservation GetTargetFolderReservation(
        string targetFolder,
        Dictionary<string, TargetFolderReservation> reservationsByFolder)
    {
        if (reservationsByFolder.TryGetValue(targetFolder, out var existing))
            return existing;

        var reservation = new TargetFolderReservation();
        if (Directory.Exists(targetFolder))
        {
            foreach (var existingFile in Directory.EnumerateFiles(targetFolder))
            {
                var existingName = Path.GetFileName(existingFile);
                if (!string.IsNullOrWhiteSpace(existingName))
                    ReserveFileName(reservation, existingName);
            }
        }

        reservationsByFolder[targetFolder] = reservation;
        return reservation;
    }

    private static bool ReserveFileName(TargetFolderReservation reservation, string fileName)
    {
        if (!reservation.ReservedNames.Add(fileName))
            return false;

        if (!TryParseNumberedAssetName(fileName, out var baseTitle, out var typeToken, out var number))
            return true;

        var prefix = $"{baseTitle}_{typeToken}_";
        var candidateNext = number + 1;

        if (reservation.NextNumberByPrefix.TryGetValue(prefix, out var current) && current >= candidateNext)
            return true;

        reservation.NextNumberByPrefix[prefix] = candidateNext;
        return true;
    }

    private static string GetRenumberedAssetFileName(string fileName, TargetFolderReservation reservation)
    {
        if (TryParseNumberedAssetName(fileName, out var baseTitle, out var typeToken, out _))
        {
            var extension = Path.GetExtension(fileName);
            var prefix = $"{baseTitle}_{typeToken}_";
            var next = GetNextAssetNumber(reservation, prefix);
            return GetUniqueNameWithPrefix(reservation, prefix, extension, next);
        }

        return GetFallbackRenamedFileName(fileName, reservation);
    }

    private static bool TryParseNumberedAssetName(
        string fileName,
        out string baseTitle,
        out string typeToken,
        out int number)
    {
        baseTitle = string.Empty;
        typeToken = string.Empty;
        number = 0;

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
            return false;

        var lastUnderscore = nameWithoutExtension.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore >= nameWithoutExtension.Length - 1)
            return false;

        var numberPart = nameWithoutExtension[(lastUnderscore + 1)..];
        if (!int.TryParse(numberPart, out number) || number < 0)
            return false;

        var beforeNumber = nameWithoutExtension[..lastUnderscore];
        var secondLastUnderscore = beforeNumber.LastIndexOf('_');
        if (secondLastUnderscore <= 0 || secondLastUnderscore >= beforeNumber.Length - 1)
            return false;

        var parsedTypeToken = beforeNumber[(secondLastUnderscore + 1)..];
        if (!AssetTypeTokens.Contains(parsedTypeToken))
            return false;

        baseTitle = beforeNumber[..secondLastUnderscore];
        if (string.IsNullOrWhiteSpace(baseTitle))
            return false;

        typeToken = parsedTypeToken;
        return true;
    }

    private static int GetNextAssetNumber(TargetFolderReservation reservation, string prefix)
    {
        if (reservation.NextNumberByPrefix.TryGetValue(prefix, out var cachedNext))
            return cachedNext;

        var max = 0;
        foreach (var name in reservation.ReservedNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = name.Substring(prefix.Length);
            var dotIndex = remainder.IndexOf('.');
            if (dotIndex <= 0)
                continue;

            var numberPart = remainder.Substring(0, dotIndex);
            if (int.TryParse(numberPart, out var number) && number > max)
                max = number;
        }

        var next = max + 1;
        reservation.NextNumberByPrefix[prefix] = next;
        return next;
    }

    private static string GetUniqueNameWithPrefix(
        TargetFolderReservation reservation,
        string prefix,
        string extension,
        int startNumber)
    {
        var counter = Math.Max(startNumber, 1);
        while (true)
        {
            var name = $"{prefix}{counter:D2}{extension}";
            if (ReserveFileName(reservation, name))
                return name;

            counter++;
        }
    }

    private static string GetFallbackRenamedFileName(string fileName, TargetFolderReservation reservation)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (true)
        {
            var candidateName = $"{baseName}_Moved_{counter:D2}{extension}";
            if (ReserveFileName(reservation, candidateName))
                return candidateName;

            counter++;
        }
    }
}
