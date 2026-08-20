using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// A stroked icon next to a label — the content of every text button in the
/// shell. See CLAUDE.md UI rule #3 (DRY): this markup was inline eight times in
/// <c>MainWindow.axaml</c>.
/// </summary>
/// <remarks>
/// <see cref="IconSize"/> and <see cref="StrokeThickness"/> are properties
/// rather than constants because the three groups of buttons were drawn at
/// different weights on purpose — 16 px at 1.6 in the topbar, 14 px in the
/// project strip, 13 px at 1.7 in the action bar. Collapsing them to one value
/// would have been a design change hiding inside a refactor.
/// </remarks>
public partial class IconButtonContent : UserControl
{
    /// <summary>The icon outline, in path mini-language.</summary>
    public static readonly StyledProperty<Geometry?> GeometryProperty =
        AvaloniaProperty.Register<IconButtonContent, Geometry?>(nameof(Geometry));

    /// <summary>The label beside it.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<IconButtonContent, string>(nameof(Text), defaultValue: string.Empty);

    /// <summary>Side of the square the icon is drawn into.</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconButtonContent, double>(nameof(IconSize), defaultValue: 16d);

    /// <summary>Outline weight.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<IconButtonContent, double>(nameof(StrokeThickness), defaultValue: 1.6d);

    public Geometry? Geometry
    {
        get => GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IconButtonContent()
    {
        InitializeComponent();
    }
}
