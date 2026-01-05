using System;
using System.Diagnostics;
using System.IO;

namespace Retromind.Services;

/// <summary>
/// Abstraction for opening external documents (manuals, guides, readme files, etc.)
/// with the system's default viewer.
/// 
/// Implementations should be best-effort only: failures must not crash the UI
/// and should be handled gracefully (logging, optional user feedback)
/// </summary>
public interface IDocumentService
{
    /// <summary>
    /// Opens the given document with the system's default viewer.
    /// The path must be an absolute file system path
    /// </summary>
    /// <param name="fullPath">Absolute path to the document to open.</param>
    void OpenDocument(string fullPath);
}