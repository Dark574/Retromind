using System;
using System.IO;

namespace Retromind.Services;

public sealed class SettingsLoadException : IOException
{
    public SettingsLoadException(Exception? primaryError, Exception? backupError)
        : base(
            "The application settings could not be loaded from either the primary file or its backup.",
            backupError ?? primaryError)
    {
        PrimaryError = primaryError;
        BackupError = backupError;
    }

    public Exception? PrimaryError { get; }

    public Exception? BackupError { get; }
}
