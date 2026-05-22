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
    private ScrollViewer? _guttersScroll;
    private ScrollBar? _sharedScrollBar;
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
        _guttersScroll = this.FindControl<ScrollViewer>("GuttersScroll");
        _sharedScrollBar = this.FindControl<ScrollBar>("SharedScrollBar");

#pragma warning disable IDE0031 // event +=/-= cannot use ?. conditional form
        if (_sourceScroll is not null) { _sourceScroll.ScrollChanged += OnSourceScrollChanged; }
        if (_targetScroll is not null) { _targetScroll.ScrollChanged += OnTargetScrollChanged; }
        if (_sharedScrollBar is not null) { _sharedScrollBar.ValueChanged += OnSharedScrollBarChanged; }
#pragma warning restore IDE0031
    }

    private void UnwireScrollSynchronisation()
    {
#pragma warning disable IDE0031 // event +=/-= cannot use ?. conditional form
        if (_sourceScroll is not null) { _sourceScroll.ScrollChanged -= OnSourceScrollChanged; }
        if (_targetScroll is not null) { _targetScroll.ScrollChanged -= OnTargetScrollChanged; }
        if (_sharedScrollBar is not null) { _sharedScrollBar.ValueChanged -= OnSharedScrollBarChanged; }
#pragma warning restore IDE0031
    }

    private void OnSourceScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync || _sourceScroll is null) { return; }

        double offsetY = _sourceScroll.Offset.Y;
        SyncTargetScrollTo(offsetY);
        SyncCenterScrollTo(offsetY);
        SyncGuttersScrollTo(offsetY);
        SyncScrollBarTo(offsetY);
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
        SyncGuttersScrollTo(offsetY);
        SyncScrollBarTo(offsetY);
    }

    private void SyncGuttersScrollTo(double offsetY)
    {
        if (_guttersScroll is null) { return; }

        _suppressScrollSync = true;
        try
        {
            _guttersScroll.Offset = new Vector(_guttersScroll.Offset.X, offsetY);
        }
        finally
        {
            _suppressScrollSync = false;
        }
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

            if (_guttersScroll is not null)
            {
                _guttersScroll.Offset = new Vector(_guttersScroll.Offset.X, offsetY);
            }
#pragma warning restore IDE0031
        }
        finally
        {
            _suppressScrollSync = false;
        }
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
