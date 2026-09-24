using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Services;

namespace Retromind.Views;

public partial class BigModeHostView
{
    private static readonly TimeSpan DynamicAccentDebounceDelay = TimeSpan.FromMilliseconds(180);
    private static readonly Color DefaultDynamicAccent = Color.Parse("#62E6FF");
    private static readonly Color DefaultDynamicSecondaryAccent = Color.Parse("#9B7BFF");

    private readonly ArtworkPaletteService _artworkPaletteService = new();
    private CancellationTokenSource? _dynamicAccentCts;
    private Control? _dynamicAccentRoot;
    private Color _dynamicAccentFallback = DefaultDynamicAccent;
    private Color _dynamicSecondaryAccentFallback = DefaultDynamicSecondaryAccent;
    private int _dynamicAccentGeneration;

    private void InitializeDynamicAccent(Control themeRoot)
    {
        CancelDynamicAccentUpdates();
        _dynamicAccentRoot = null;

        if (!ThemeProperties.GetDynamicAccentEnabled(themeRoot))
            return;

        _dynamicAccentRoot = themeRoot;
        _dynamicAccentFallback = themeRoot.IsSet(ThemeProperties.DynamicAccentColorProperty)
            ? ThemeProperties.GetDynamicAccentColor(themeRoot)
            : ThemeProperties.GetAccentColor(themeRoot) ?? DefaultDynamicAccent;
        _dynamicSecondaryAccentFallback = themeRoot.IsSet(ThemeProperties.DynamicSecondaryAccentColorProperty)
            ? ThemeProperties.GetDynamicSecondaryAccentColor(themeRoot)
            : DefaultDynamicSecondaryAccent;

        SetDynamicAccentColors(themeRoot, _dynamicAccentFallback, _dynamicSecondaryAccentFallback);
        RequestDynamicAccentUpdate(themeRoot);
    }

    private void RequestDynamicAccentUpdate(Control themeRoot)
    {
        if (!ReferenceEquals(themeRoot, _dynamicAccentRoot) ||
            !ThemeProperties.GetDynamicAccentEnabled(themeRoot) ||
            DataContext is not Retromind.ViewModels.BigModeViewModel vm)
        {
            return;
        }

        _dynamicAccentCts?.Cancel();
        _dynamicAccentCts?.Dispose();

        var cts = new CancellationTokenSource();
        _dynamicAccentCts = cts;
        var generation = ++_dynamicAccentGeneration;
        var artworkPath = vm.DynamicAccentArtworkPath;

        _ = UpdateDynamicAccentAsync(themeRoot, artworkPath, generation, cts.Token);
    }

    private async Task UpdateDynamicAccentAsync(
        Control themeRoot,
        string? artworkPath,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DynamicAccentDebounceDelay, cancellationToken).ConfigureAwait(false);

            var palette = await _artworkPaletteService
                .GetPaletteAsync(artworkPath, cancellationToken)
                .ConfigureAwait(false);

            var targetAccent = palette?.Accent ?? _dynamicAccentFallback;
            var targetSecondaryAccent = palette?.SecondaryAccent ?? _dynamicSecondaryAccentFallback;

            Color startAccent = default;
            Color startSecondaryAccent = default;
            var transitionMs = 0;

            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!IsCurrentDynamicAccentRequest(themeRoot, generation, cancellationToken))
                    return;

                startAccent = ThemeProperties.GetDynamicAccentColor(themeRoot);
                startSecondaryAccent = ThemeProperties.GetDynamicSecondaryAccentColor(themeRoot);
                transitionMs = Math.Clamp(
                    ThemeProperties.GetDynamicAccentTransitionMs(themeRoot),
                    0,
                    5_000);
            });

            if (!IsCurrentDynamicAccentRequest(themeRoot, generation, cancellationToken))
                return;

            if (transitionMs == 0)
            {
                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (IsCurrentDynamicAccentRequest(themeRoot, generation, cancellationToken))
                        SetDynamicAccentColors(themeRoot, targetAccent, targetSecondaryAccent);
                });
                return;
            }

            var startedAt = DateTime.UtcNow;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var progress = Math.Clamp(
                    (DateTime.UtcNow - startedAt).TotalMilliseconds / transitionMs,
                    0,
                    1);
                var eased = 1 - Math.Pow(1 - progress, 3);
                var accent = Interpolate(startAccent, targetAccent, eased);
                var secondaryAccent = Interpolate(startSecondaryAccent, targetSecondaryAccent, eased);

                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (IsCurrentDynamicAccentRequest(themeRoot, generation, cancellationToken))
                        SetDynamicAccentColors(themeRoot, accent, secondaryAccent);
                });

                if (progress >= 1)
                    break;

                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this palette request.
        }
    }

    private bool IsCurrentDynamicAccentRequest(
        Control themeRoot,
        int generation,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        generation == _dynamicAccentGeneration &&
        ReferenceEquals(themeRoot, _dynamicAccentRoot);

    private static void SetDynamicAccentColors(Control themeRoot, Color accent, Color secondaryAccent)
    {
        ThemeProperties.SetDynamicAccentColor(themeRoot, accent);
        ThemeProperties.SetDynamicSecondaryAccentColor(themeRoot, secondaryAccent);

        // AccentColor remains the canonical selection-glow color for existing
        // theme bindings and follows the dynamic primary accent when opted in.
        ThemeProperties.SetAccentColor(themeRoot, accent);
    }

    private void CancelDynamicAccentUpdates()
    {
        _dynamicAccentGeneration++;
        _dynamicAccentCts?.Cancel();
        _dynamicAccentCts?.Dispose();
        _dynamicAccentCts = null;
    }

    private static Color Interpolate(Color from, Color to, double progress)
    {
        static byte Channel(byte start, byte end, double amount) =>
            (byte)Math.Clamp(Math.Round(start + ((end - start) * amount)), 0, 255);

        return Color.FromArgb(
            Channel(from.A, to.A, progress),
            Channel(from.R, to.R, progress),
            Channel(from.G, to.G, progress),
            Channel(from.B, to.B, progress));
    }
}
