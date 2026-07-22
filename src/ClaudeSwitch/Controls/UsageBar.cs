using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ClaudeSwitch.Controls;

/// <summary>
/// The 5-hour / 7-day usage bar, drawn so it eases to a new value instead of jumping.
///
/// Replaces the two-column GridLength trick the bars used before: GridLength can't be animated, so
/// a refreshed number snapped. Sharing <see cref="AnimatedPercent"/> with the ring, the fill now
/// grows in step with the ring and the number ticking up beside it.
/// </summary>
internal sealed class UsageBar : AnimatedPercent
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radius = h / 2;

        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);

        var fraction = Math.Clamp(ShownPercent, 0, 100) / 100.0;
        var fillWidth = w * fraction;
        if (fillWidth <= 0) return;

        // Never draw the rounded fill narrower than its own corner diameter, or the two end arcs
        // overlap into a lens shape at low percentages.
        fillWidth = Math.Max(fillWidth, Math.Min(h, w));
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fillWidth, h), radius, radius);
    }
}
