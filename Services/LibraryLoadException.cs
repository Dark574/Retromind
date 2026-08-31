using System;
using System.IO;

namespace Retromind.Services;

public sealed class LibraryLoadException : IOException
{
    public LibraryLoadException(Exception? primaryError, Exception? backupError)
        : base(
            "The media library could not be loaded from either the primary file or its backup.",
            backupError ?? primaryError)
    {
        PrimaryError = primaryError;
        BackupError = backupError;
    }

    public Exception? PrimaryError { get; }

    public Exception? BackupError { get; }
}
