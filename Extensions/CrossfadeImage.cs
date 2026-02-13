using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Retromind.Helpers;

namespace Retromind.Extensions;

public class CrossfadeImage : Grid
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<CrossfadeImage, string?>(nameof(Url));

    public static readonly StyledProperty<int?> DecodeWidthProperty =
        AvaloniaProperty.Register<CrossfadeImage, int?>(nameof(DecodeWidth));

    public static readonly StyledProperty<bool> DisableCacheProperty =
        AvaloniaProperty.Register<CrossfadeImage, bool>(nameof(DisableCache));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CrossfadeImage, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<int> FadeDurationMsProperty =
        AvaloniaProperty.Register<CrossfadeImage, int>(nameof(FadeDurationMs), 0);

    public static readonly StyledProperty<int> FadeDelayMsProperty =
        AvaloniaProperty.Register<CrossfadeImage, int>(nameof(FadeDelayMs), 0);

    private readonly Image _imageA;
    private readonly Image _imageB;
    private int _activeIndex;
    private int _loadGeneration;
    private int _imageAGeneration;
    private int _imageBGeneration;
    private string? _currentUrl;
    private static readonly TimeSpan LoadHoldDelay = TimeSpan.FromMilliseconds(140);

    static CrossfadeImage()
    {
        UrlProperty.Changed.AddClassHandler<CrossfadeImage>((c, e) =>
            c.StartCrossfadeToUrl((string?)e.NewValue, forceReload: false));

        DecodeWidthProperty.Changed.AddClassHandler<CrossfadeImage>((c, _) =>
            c.StartCrossfadeToUrl(c.Url, forceReload: true));

        DisableCacheProperty.Changed.AddClassHandler<CrossfadeImage>((c, _) =>
            c.StartCrossfadeToUrl(c.Url, forceReload: true));

        StretchProperty.Changed.AddClassHandler<CrossfadeImage>((c, e) =>
            c.ApplyStretch((Stretch)e.NewValue!));

        FadeDurationMsProperty.Changed.AddClassHandler<CrossfadeImage>((c, _) =>
            c.UpdateTransitions());

        FadeDelayMsProperty.Changed.AddClassHandler<CrossfadeImage>((c, _) =>
            c.StartCrossfadeToUrl(c.Url, forceReload: true));
    }

    public CrossfadeImage()
    {
        _imageA = CreateImage(0);
        _imageB = CreateImage(1);

        Children.Add(_imageA);
        Children.Add(_imageB);

        _imageA.PropertyChanged += OnInternalImagePropertyChanged;
        _imageB.PropertyChanged += OnInternalImagePropertyChanged;

        UpdateTransitions();
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public int? DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public bool DisableCache
    {
        get => GetValue(DisableCacheProperty);
        set => SetValue(DisableCacheProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public int FadeDurationMs
    {
        get => GetValue(FadeDurationMsProperty);
        set => SetValue(FadeDurationMsProperty, value);
    }

    public int FadeDelayMs
    {
        get => GetValue(FadeDelayMsProperty);
        set => SetValue(FadeDelayMsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _imageA.Measure(availableSize);
        _imageB.Measure(availableSize);

        var active = _activeIndex == 0 ? _imageA : _imageB;
        return active.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = new Rect(finalSize);
        _imageA.Arrange(bounds);
        _imageB.Arrange(bounds);
        return finalSize;
    }

    private static Image CreateImage(int zIndex)
    {
        var image = new Image
        {
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        image.SetValue(Panel.ZIndexProperty, zIndex);
        return image;
    }

    private void ApplyStretch(Stretch stretch)
    {
        _imageA.Stretch = stretch;
        _imageB.Stretch = stretch;
    }

    private void StartCrossfadeToUrl(string? url, bool forceReload)
    {
        if (!forceReload && string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase))
            return;

        _currentUrl = url;

        if (string.IsNullOrWhiteSpace(url))
        {
            ClearImages();
            return;
        }

        EnsureImagesVisible();

        var target = GetInactiveImage(out var targetIndex);
        var old = GetActiveImage();

        var generation = ++_loadGeneration;
        if (targetIndex == 0)
            _imageAGeneration = generation;
        else
            _imageBGeneration = generation;

        UpdateTransitions();

        target.Opacity = 0;
        old.Opacity = 1;

        ApplyImageSettings(target);

        if (!forceReload &&
            string.Equals(AsyncImageHelper.GetUrl(target), url, StringComparison.OrdinalIgnoreCase) &&
            AsyncImageHelper.GetIsLoaded(target))
        {
            // Target image already has the correct URL loaded: still crossfade for consistency.
            _ = ApplyLoadedImageAsync(target, targetIndex, generation);
            return;
        }

        AsyncImageHelper.SetUrl(target, url);
        ScheduleFallbackFadeOut(old, generation, targetIndex, ResolveFallbackHoldDelay());
    }

    private void ClearImages()
    {
        AsyncImageHelper.SetUrl(_imageA, null);
        AsyncImageHelper.SetUrl(_imageB, null);
        _imageA.Opacity = 0;
        _imageB.Opacity = 0;
        _imageA.IsVisible = false;
        _imageB.IsVisible = false;
    }

    private void ApplyImageSettings(Image target)
    {
        AsyncImageHelper.SetDecodeWidth(target, DecodeWidth);
        AsyncImageHelper.SetDisableCache(target, DisableCache);
    }

    private void OnInternalImagePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != AsyncImageHelper.IsLoadedProperty)
            return;

        if (sender is not Image image)
            return;

        if (!AsyncImageHelper.GetIsLoaded(image))
            return;

        var generation = ReferenceEquals(image, _imageA) ? _imageAGeneration : _imageBGeneration;
        if (generation != _loadGeneration)
            return;

        var targetIndex = ReferenceEquals(image, _imageA) ? 0 : 1;
        _ = ApplyLoadedImageAsync(image, targetIndex, generation);
    }

    private async void ScheduleFallbackFadeOut(Image old, int generation, int targetIndex, TimeSpan holdDelay)
    {
        try
        {
            await Task.Delay(holdDelay).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        UiThreadHelper.Post(() =>
        {
            if (generation != _loadGeneration)
                return;

            if (_activeIndex == targetIndex)
                return;

            old.Opacity = 0;
        });
    }

    private async Task ApplyLoadedImageAsync(Image image, int targetIndex, int generation)
    {
        var delayMs = FadeDelayMs;
        if (delayMs > 0)
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }

        if (generation != _loadGeneration)
            return;

        var other = targetIndex == 0 ? _imageB : _imageA;

        UiThreadHelper.Post(() =>
        {
            if (generation != _loadGeneration)
                return;

            image.Opacity = 1;
            other.Opacity = 0;

            _activeIndex = targetIndex;
            ScheduleDeactivateInactiveImage(generation, targetIndex);
        });
    }

    private TimeSpan ResolveFallbackHoldDelay()
    {
        var delayMs = FadeDelayMs;
        if (delayMs <= 0)
            return LoadHoldDelay;

        var hold = (int)LoadHoldDelay.TotalMilliseconds;
        var ms = Math.Max(delayMs, hold);
        return TimeSpan.FromMilliseconds(ms);
    }

    private Image GetActiveImage() => _activeIndex == 0 ? _imageA : _imageB;

    private Image GetInactiveImage(out int index)
    {
        if (_activeIndex == 0)
        {
            index = 1;
            return _imageB;
        }

        index = 0;
        return _imageA;
    }

    private void UpdateTransitions()
    {
        var duration = ResolveFadeDuration();
        EnsureOpacityTransition(_imageA, duration);
        EnsureOpacityTransition(_imageB, duration);
    }

    private TimeSpan ResolveFadeDuration()
    {
        var ms = FadeDurationMs;
        if (ms <= 0)
        {
            var ancestor = this.GetVisualParent();
            UserControl? themeRoot = null;
            while (ancestor != null)
            {
                if (ancestor is UserControl uc)
                {
                    themeRoot = uc;
                    break;
                }
                ancestor = ancestor.GetVisualParent();
            }

            if (themeRoot != null)
                ms = ThemeProperties.GetFadeDurationMs(themeRoot);
        }

        if (ms <= 0)
            ms = 250;

        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 10000));
    }

    private static void EnsureOpacityTransition(Control target, TimeSpan duration)
    {
        var transitions = target.Transitions ?? new Transitions();
        target.Transitions = transitions;

        DoubleTransition? opacityTransition = null;
        foreach (var t in transitions)
        {
            if (t is DoubleTransition dt && Equals(dt.Property, Visual.OpacityProperty))
            {
                opacityTransition = dt;
                break;
            }
        }

        if (opacityTransition == null)
        {
            opacityTransition = new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new CubicEaseOut()
            };
            transitions.Add(opacityTransition);
        }
        else
        {
            opacityTransition.Duration = duration;
            opacityTransition.Easing ??= new CubicEaseOut();
        }
    }

    private void EnsureImagesVisible()
    {
        _imageA.IsVisible = true;
        _imageB.IsVisible = true;
    }

    private void SetActiveVisibility(int activeIndex)
    {
        if (activeIndex == 0)
        {
            _imageA.IsVisible = true;
            _imageB.IsVisible = false;
        }
        else
        {
            _imageA.IsVisible = false;
            _imageB.IsVisible = true;
        }
    }

    private void ScheduleDeactivateInactiveImage(int generation, int activeIndex)
    {
        var duration = ResolveFadeDuration();
        var delayMs = (int)duration.TotalMilliseconds;
        if (delayMs <= 0)
        {
            SetActiveVisibility(activeIndex);
            return;
        }

        _ = DeactivateInactiveAfterDelayAsync(generation, activeIndex, delayMs);
    }

    private async Task DeactivateInactiveAfterDelayAsync(int generation, int activeIndex, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        UiThreadHelper.Post(() =>
        {
            if (generation != _loadGeneration)
                return;

            if (_activeIndex != activeIndex)
                return;

            SetActiveVisibility(activeIndex);
        });
    }
}
