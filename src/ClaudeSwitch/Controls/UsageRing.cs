using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ClaudeSwitch.Controls;

/// <summary>
/// A thin progress ring drawn around an account's avatar, showing its 5-hour usage.
///
/// It turns the avatar — otherwise a purely decorative initial — into a silent gauge: the fuller
/// the ring, the closer that account is to its limit, in the same green→amber→red the usage bars
/// use. No text, which is the point.
/// </summary>
internal sealed class UsageRing : AnimatedPercent
{
    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Hides the ring entirely — used when usage is unknown or rings are turned off.</summary>
    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.Register(
        nameof(ShowRing), typeof(bool), typeof(UsageRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool ShowRing { get => (bool)GetValue(ShowRingProperty); set => SetValue(ShowRingProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (!ShowRing) return;

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;

        var trackPen = new Pen(TrackBrush, Thickness);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, centre, radius, radius);

        var fraction = Math.Clamp(ShownPercent, 0, 100) / 100.0;
        if (fraction <= 0) return;

        var pen = new Pen(ProgressBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        // A full ring can't be drawn as one arc (start == end reads as a zero-length arc), so
        // draw it as a plain circle and bail.
        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        // Sweep clockwise from twelve o'clock.
        var start = new Point(centre.X, centre.Y - radius);
        var angle = fraction * 2 * Math.PI;
        var end = new Point(
            centre.X + radius * Math.Sin(angle),
            centre.Y - radius * Math.Cos(angle));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            end, new Size(radius, radius), 0,
            isLargeArc: fraction > 0.5, SweepDirection.Clockwise, isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        dc.DrawGeometry(null, pen, geometry);
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "UsageRing {0:0}%", ShownPercent);
}
