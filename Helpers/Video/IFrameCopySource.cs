using System;

namespace Retromind.Helpers.Video;

/// <summary>
/// Optional interface for video surfaces that can safely copy frames
/// under their own synchronization (avoids race conditions on native buffers).
/// </summary>
public interface IFrameCopySource
{
    /// <summary>
    /// Copies the current frame into the destination buffer.
    /// Returns false if no frame is available.
    /// </summary>
    bool CopyFrameTo(IntPtr destination, int destinationSize);
}
