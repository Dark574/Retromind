namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    /// <summary>
    /// Clears cached preview path lookups. Useful after assets changed in the CoreApp.
    /// </summary>
    public void InvalidatePreviewCaches(bool stopCurrentPreview = false)
    {
        _itemVideoPathCache.Clear();
        _nodeVideoPathCache.Clear();
        _currentPreviewVideoPath = null;

        if (stopCurrentPreview)
            StopVideo();
    }
}