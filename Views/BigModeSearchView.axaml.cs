using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class BigModeSearchView : UserControl
{
    private const double ResultRowHeight = 112;
    private readonly TextBox _text;
    private readonly ListBox _results;
    private BigModeSearchViewModel? _model;
    private bool _scrolling;
    public BigModeSearchView()
    {
        AvaloniaXamlLoader.Load(this);
        _text = this.FindControl<TextBox>("SearchTextBox")!;
        _results = this.FindControl<ListBox>("SearchResultsList")!;
        _text.AddHandler(PointerPressedEvent, (_, _) => _model?.FocusKeyboard(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _results.AddHandler(ScrollViewer.ScrollChangedEvent, OnResultsScroll);
        _results.AddHandler(PointerPressedEvent, OnResultsPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        this.FindControl<Border>("SearchScopeRegion")!.AddHandler(PointerPressedEvent, OnScopePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (_model != null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as BigModeSearchViewModel;
            if (_model != null) _model.PropertyChanged += OnModelChanged;
            FocusRegion();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_model == null && DataContext is BigModeSearchViewModel model)
            { _model = model; _model.PropertyChanged += OnModelChanged; }
            FocusRegion();
        };
        DetachedFromVisualTree += (_, _) => { if (_model != null) _model.PropertyChanged -= OnModelChanged; _model = null; };
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BigModeSearchViewModel.FocusArea)) FocusRegion();
        if (e.PropertyName is nameof(BigModeSearchViewModel.FirstVisibleResultIndex) or nameof(BigModeSearchViewModel.ResultsTitle))
            Dispatcher.UIThread.Post(SynchronizeScroll, DispatcherPriority.Loaded);
    }
    private void FocusRegion()
    {
        var model = _model;
        Dispatcher.UIThread.Post(() =>
        {
            if (model == null || !ReferenceEquals(model, _model) || !IsEffectivelyVisible) return;
            if (model.IsResultsFocused) _results.Focus();
            else if (model.IsKeyboardFocused) _text.Focus();
            else Focus();
        }, DispatcherPriority.Loaded);
    }
    private void SynchronizeScroll()
    {
        if (_model == null || _scrolling) return;
        var viewer = _results.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer == null) return;
        _scrolling = true;
        try { viewer.Offset = new(viewer.Offset.X, _model.FirstVisibleResultIndex * ResultRowHeight); }
        finally { _scrolling = false; }
    }
    private void OnResultsScroll(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrolling || _model == null) return;
        var viewer = _results.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer == null) return;
        var first = Math.Clamp((int)Math.Round(viewer.Offset.Y / ResultRowHeight), 0,
            Math.Max(0, _model.Results.Count - BigModeSearchViewModel.VisibleResultCount));
        _model.FirstVisibleResultIndex = first;
        SynchronizeScroll();
    }
    private void OnKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_model == null || sender is not Button { DataContext: BigModeSearchKey key }) return;
        _model.FocusKeyboard(); _model.SelectedKeyIndex = _model.Keys.IndexOf(key); _model.PressKey(key);
        e.Handled = true;
    }
    private void OnTextFocus(object? sender, RoutedEventArgs e) => _model?.FocusKeyboard();
    private void OnScopePointerPressed(object? sender, PointerPressedEventArgs e) { if (_model != null) _model.FocusArea = BigModeSearchFocus.Scope; }
    private void OnResultsPointerPressed(object? sender, PointerPressedEventArgs e) => _model?.FocusResults();
    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) { _model?.Confirm(); e.Handled = true; }
}
