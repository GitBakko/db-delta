using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;

namespace DbDelta.App.Views;

/// <summary>
/// Code-behind for the dual-pane SQL diff viewer. Handles scroll synchronisation
/// and minimap rectangle positioning that cannot be expressed cleanly in pure AXAML.
/// </summary>
public partial class DiffViewerView : UserControl
{
    /// <summary>Fixed line height in logical pixels — must match AXAML Height="18".</summary>
    private const double LineHeight = 18.0;

    private ScrollViewer? _sourceScroll;
    private ScrollViewer? _targetScroll;
    private ScrollViewer? _centerScroll;
    private ScrollBar? _sharedScrollBar;
    private Panel? _minimapStrip;
    private ItemsControl? _sourceMarks;
    private ItemsControl? _targetMarks;
    private Border? _viewportRect;
    private bool _suppressScrollSync;

    public DiffViewerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is DiffViewerViewModel vm)
        {
            vm.NavigateToRowRequested -= OnNavigateToRowRequested;
            vm.NavigateToRowRequested += OnNavigateToRowRequested;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireScrollSynchronisation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (DataContext is DiffViewerViewModel vm)
        {
            vm.NavigateToRowRequested -= OnNavigateToRowRequested;
        }

        UnwireScrollSynchronisation();
    }

    private void WireScrollSynchronisation()
    {
        _sourceScroll = this.FindControl<ScrollViewer>("SourceScroll");
        _targetScroll = this.FindControl<ScrollViewer>("TargetScroll");
        _centerScroll = this.FindControl<ScrollViewer>("CenterScroll");
        _sharedScrollBar = this.FindControl<ScrollBar>("SharedScrollBar");
        _minimapStrip = this.FindControl<Panel>("MinimapStrip");
        _sourceMarks = this.FindControl<ItemsControl>("SourceMarks");
        _targetMarks = this.FindControl<ItemsControl>("TargetMarks");
        _viewportRect = this.FindControl<Border>("ViewportRect");

#pragma warning disable IDE0031 // event +=/-= cannot use ?. conditional form
        if (_sourceScroll is not null) { _sourceScroll.ScrollChanged += OnSourceScrollChanged; }
        if (_targetScroll is not null) { _targetScroll.ScrollChanged += OnTargetScrollChanged; }
        if (_sharedScrollBar is not null) { _sharedScrollBar.ValueChanged += OnSharedScrollBarChanged; }
        if (_minimapStrip is not null) { _minimapStrip.LayoutUpdated += OnMinimapStripLayoutUpdated; }
        if (_sourceMarks is not null) { _sourceMarks.LayoutUpdated += OnMinimapStripLayoutUpdated; }
#pragma warning restore IDE0031
    }

    private void UnwireScrollSynchronisation()
    {
#pragma warning disable IDE0031 // event +=/-= cannot use ?. conditional form
        if (_sourceScroll is not null) { _sourceScroll.ScrollChanged -= OnSourceScrollChanged; }
        if (_targetScroll is not null) { _targetScroll.ScrollChanged -= OnTargetScrollChanged; }
        if (_sharedScrollBar is not null) { _sharedScrollBar.ValueChanged -= OnSharedScrollBarChanged; }
        if (_minimapStrip is not null) { _minimapStrip.LayoutUpdated -= OnMinimapStripLayoutUpdated; }
        if (_sourceMarks is not null) { _sourceMarks.LayoutUpdated -= OnMinimapStripLayoutUpdated; }
#pragma warning restore IDE0031
    }

    private void OnSourceScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync || _sourceScroll is null) { return; }

        double offsetY = _sourceScroll.Offset.Y;
        SyncTargetScrollTo(offsetY);
        SyncCenterScrollTo(offsetY);
        SyncScrollBarTo(offsetY);
        UpdateViewportRect();
    }

    /// <summary>
    /// Mirror of <see cref="OnSourceScrollChanged"/> so a mouse wheel hit on
    /// the target pane also drives the shared scrollbar, the source pane and
    /// the centre actions column — keeps all four columns locked together.
    /// </summary>
    private void OnTargetScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync || _targetScroll is null) { return; }

        double offsetY = _targetScroll.Offset.Y;
        SyncSourceScrollTo(offsetY);
        SyncCenterScrollTo(offsetY);
        SyncScrollBarTo(offsetY);
        UpdateViewportRect();
    }

    private void SyncSourceScrollTo(double offsetY)
    {
        if (_sourceScroll is null) { return; }

        _suppressScrollSync = true;
        try
        {
            _sourceScroll.Offset = new Vector(_sourceScroll.Offset.X, offsetY);
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    private void SyncCenterScrollTo(double offsetY)
    {
        if (_centerScroll is null) { return; }

        _suppressScrollSync = true;
        try
        {
            _centerScroll.Offset = new Vector(_centerScroll.Offset.X, offsetY);
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    private void OnSharedScrollBarChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressScrollSync) { return; }
        SyncBothScrollsTo(e.NewValue);
    }

    private void SyncTargetScrollTo(double offsetY)
    {
        if (_targetScroll is null) { return; }

        _suppressScrollSync = true;
        try
        {
            _targetScroll.Offset = new Vector(_targetScroll.Offset.X, offsetY);
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    private void SyncBothScrollsTo(double offsetY)
    {
        _suppressScrollSync = true;
        try
        {
#pragma warning disable IDE0031 // property assignment cannot use ?. conditional form
            if (_sourceScroll is not null)
            {
                _sourceScroll.Offset = new Vector(_sourceScroll.Offset.X, offsetY);
            }

            if (_targetScroll is not null)
            {
                _targetScroll.Offset = new Vector(_targetScroll.Offset.X, offsetY);
            }

            if (_centerScroll is not null)
            {
                _centerScroll.Offset = new Vector(_centerScroll.Offset.X, offsetY);
            }
#pragma warning restore IDE0031
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    /// <summary>
    /// Layout pass on the minimap strip — re-positions per-row marks and
    /// recomputes the viewport rectangle. Fires whenever the strip resizes
    /// or new mark containers materialise from the ItemsControl.
    /// </summary>
    private void OnMinimapStripLayoutUpdated(object? sender, EventArgs e)
    {
        PositionMinimapMarks();
        UpdateViewportRect();
    }

    /// <summary>
    /// Lays out the per-row diff marks inside the minimap strip. Each diff
    /// row maps to a Y position scaled by stripHeight / contentHeight. Source
    /// marks sit at x=1 (left half), target marks at x=11 (right half) — both
    /// 8 px wide, 2 px tall, with a 2 px gap. Non-diff rows have invisible
    /// rectangles and are skipped here.
    /// </summary>
    private void PositionMinimapMarks()
    {
        if (_minimapStrip is null || DataContext is not DiffViewerViewModel vm) { return; }

        double stripHeight = _minimapStrip.Bounds.Height;
        if (stripHeight <= 0 || vm.Rows.Count == 0) { return; }

        double contentHeight = vm.Rows.Count * LineHeight;
        double scale = stripHeight / contentHeight;

        if (_sourceMarks is not null)
        {
            PositionMarksColumn(_sourceMarks, vm.Rows.Count, scale, leftPx: 1);
        }
        if (_targetMarks is not null)
        {
            PositionMarksColumn(_targetMarks, vm.Rows.Count, scale, leftPx: 11);
        }
    }

    private static void PositionMarksColumn(ItemsControl marks, int rowCount, double scale, double leftPx)
    {
        for (int i = 0; i < rowCount; i++)
        {
            if (marks.ContainerFromIndex(i) is not Control container) { continue; }
            double top = i * LineHeight * scale;
            Canvas.SetTop(container, top);
            Canvas.SetLeft(container, leftPx);
        }
    }

    /// <summary>
    /// Repositions the semi-transparent overlay rectangle so it mirrors the
    /// source pane's currently-visible viewport — analogous to SQL Compare's
    /// "you are here" indicator. Top + Height are scaled from scroll offset
    /// and viewport height against the minimap strip size.
    /// </summary>
    private void UpdateViewportRect()
    {
        if (_viewportRect is null || _minimapStrip is null || _sourceScroll is null) { return; }

        double stripHeight = _minimapStrip.Bounds.Height;
        double extent = _sourceScroll.Extent.Height;
        if (stripHeight <= 0 || extent <= 0) { return; }

        double scale = stripHeight / extent;
        double top = Math.Max(0, _sourceScroll.Offset.Y * scale);
        double height = Math.Max(8, _sourceScroll.Viewport.Height * scale);

        // Clamp so the rectangle never spills past the strip.
        if (top + height > stripHeight) { top = Math.Max(0, stripHeight - height); }

        _viewportRect.Margin = new Thickness(0, top, 0, 0);
        _viewportRect.Height = height;
    }

    private void SyncScrollBarTo(double offsetY)
    {
        if (_sharedScrollBar is null || _sourceScroll is null) { return; }

        _suppressScrollSync = true;
        try
        {
            double extent = _sourceScroll.Extent.Height;
            double viewport = _sourceScroll.Viewport.Height;
            _sharedScrollBar.Maximum = Math.Max(0, extent - viewport);
            _sharedScrollBar.ViewportSize = viewport;
            _sharedScrollBar.Value = offsetY;
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    private void OnNavigateToRowRequested(object? sender, int rowIndex)
    {
        double targetOffset = rowIndex * LineHeight;
        Dispatcher.UIThread.Post(() =>
        {
            SyncBothScrollsTo(targetOffset);
            SyncScrollBarTo(targetOffset);
        });
    }

    /// <summary>
    /// Computes the background brush for a diff row in the source or target pane.
    /// Exposed as a static helper for code-behind callers; the AXAML view uses
    /// <see cref="Converters.LineStatusToSourceBackground"/> and
    /// <see cref="Converters.LineStatusToTargetBackground"/> instead.
    /// </summary>
    public static Avalonia.Media.IBrush GetRowBackground(LineStatus status, bool isSourcePane) =>
        status switch
        {
            LineStatus.Added when isSourcePane => Avalonia.Media.Brushes.Transparent,
            LineStatus.Added => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CDFFD6")),
            LineStatus.Removed when !isSourcePane => Avalonia.Media.Brushes.Transparent,
            LineStatus.Removed => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFCED3")),
            LineStatus.Modified => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CFE2FF")),
            LineStatus.Unchanged => Avalonia.Media.Brushes.Transparent,
            _ => Avalonia.Media.Brushes.Transparent,
        };
}
