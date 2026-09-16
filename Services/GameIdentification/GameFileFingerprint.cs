using System;

namespace Retromind.Services.GameIdentification;

/// <summary>
/// Provider-neutral checksums and file identity captured during a single read.
/// These values are not equivalent to a system-specific RetroAchievements hash.
/// </summary>
public sealed record GameFileFingerprint(
    long FileSize,
    DateTime LastWriteTimeUtc,
    string Crc32,
    string Md5,
    string Sha1);
