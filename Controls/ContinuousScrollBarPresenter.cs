using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls;

/// <summary>在原生滚动条的原模板位置承载连续滚动条，保留 ScrollViewer 的布局、滚轮、键盘和虚拟化。</summary>
public sealed class ContinuousScrollBarPresenter : Grid
{
    private readonly ContinuousScrollBar _bar = new() { Step = 0 };
    private readonly List<(DependencyProperty Property, long Token)> _subscriptions = [];
    private ScrollBar? _source;
    private ScrollViewer? _viewer;
    private bool _syncing, _interacting, _hovered;
    private (Orientation Orientation, double Size)? _geometry;
    private bool? _expanded;

    public ContinuousScrollBarPresenter()
    {
        Children.Add(_bar);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _bar.ValueChanged += ScrollTo;
        _bar.InteractionStarted += () => _interacting = true;
        _bar.InteractionCompleted += () => { _interacting = false; Synchronize(); };
        _bar.HoverChanged += hovered => { _hovered = hovered; UpdateAppearance(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_source is not null) return;
        _source = Ancestor<ScrollBar>(this);
        if (_source is null) return;
        _viewer = Ancestor<ScrollViewer>(_source);
        if (_viewer is not null)
        {
            _viewer.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnViewerWheel), true);
            _viewer.AddHandler(KeyDownEvent, new KeyEventHandler(OnViewerKey), true);
        }
        foreach (var property in new[]
        {
            RangeBase.MinimumProperty, RangeBase.MaximumProperty, RangeBase.ValueProperty,
            ScrollBar.ViewportSizeProperty, ScrollBar.OrientationProperty, ScrollBar.IndicatorModeProperty,
            Control.IsEnabledProperty,
        })
            _subscriptions.Add((property, _source.RegisterPropertyChangedCallback(property, (_, _) => Synchronize())));
        Synchronize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_source is not null)
            foreach (var subscription in _subscriptions)
                _source.UnregisterPropertyChangedCallback(subscription.Property, subscription.Token);
        _subscriptions.Clear();
        if (_viewer is not null)
        {
            _viewer.RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(OnViewerWheel));
            _viewer.RemoveHandler(KeyDownEvent, new KeyEventHandler(OnViewerKey));
        }
        _geometry = null;
        _expanded = null;
        _source = null;
        _viewer = null;
        _interacting = false;
        _hovered = false;
        _bar.CancelInteraction();
    }

    private void OnViewerWheel(object sender, PointerRoutedEventArgs args)
    {
        _bar.CancelInteraction();
        _bar.NotifyActivity();
    }

    private void OnViewerKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
            or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End)
        {
            _bar.CancelInteraction();
            _bar.NotifyActivity();
        }
    }

    private void Synchronize()
    {
        if (_source is null) return;
        _syncing = true;
        try
        {
            var vertical = _source.Orientation == Orientation.Vertical;
            var size = ResourceNumber("ScrollBarSize", 12);
            var geometry = (_source.Orientation, size);
            if (_geometry != geometry)
            {
                _geometry = geometry;
                _expanded = null;
                _bar.Orientation = _source.Orientation;
                _bar.Width = double.NaN;
                _bar.Height = double.NaN;
                _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                _bar.VerticalAlignment = VerticalAlignment.Stretch;
                _bar.Margin = vertical ? new Thickness(0, size, 0, size) : new Thickness(size, 0, size, 0);
                _bar.MinimumThumbLength = ResourceNumber(vertical ? "ScrollBarVerticalThumbMinHeight" : "ScrollBarHorizontalThumbMinWidth", 30);
                Width = vertical ? size : double.NaN;
                Height = vertical ? double.NaN : size;
            }
            _bar.Minimum = _source.Minimum;
            _bar.Maximum = _source.Maximum;
            _bar.ViewportSize = _source.ViewportSize;
            if (!_interacting && Math.Abs(_bar.Value - _source.Value) > .01) _bar.Value = _source.Value;
            UpdateAppearance();
        }
        finally { _syncing = false; }
    }

    private void UpdateAppearance()
    {
        if (_source is null) return;
        var expanded = _hovered || _interacting || _source.IndicatorMode == ScrollingIndicatorMode.MouseIndicator;
        var size = ResourceNumber("ScrollBarSize", 12);
        var vertical = _source.Orientation == Orientation.Vertical;
        if (_expanded != expanded)
        {
            _expanded = expanded;
            _bar.ThumbCrossOffset = expanded ? 0 : (size - 2) / 2 - 2;
            _bar.ThumbThickness = expanded ? ResourceNumber(_source.Orientation == Orientation.Vertical
                ? "ScrollBarVerticalThumbMinWidth" : "ScrollBarHorizontalThumbMinHeight", 8) : 2;
            _bar.TrackThickness = expanded ? 2 : 1;
        }
        _bar.IsHitTestVisible = _source.IsEnabled && _source.Maximum > _source.Minimum;
    }

    private void ScrollTo(double value)
    {
        if (_syncing || _source is null) return;
        if (_viewer is null) return;
        if (_source.Orientation == Orientation.Vertical) _viewer.ChangeView(null, value, null, true);
        else _viewer.ChangeView(value, null, null, true);
    }

    private static T? Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static double ResourceNumber(string key, double fallback)
        => Application.Current.Resources.TryGetValue(key, out var resource) && resource is double number ? number : fallback;
}
