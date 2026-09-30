using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClaudeSwitch.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ClaudeSwitch.Controls;

/// <summary>
/// A thin arc chasing its tail around a faint track — the "working on it" indicator.
///
/// It spins only while it is actually on screen: the rotation starts when the control becomes
/// visible and is torn down the moment it isn't. A forever-animation left running in a hidden
/// panel keeps the render loop awake, which is exactly wrong for an app that lives in the tray.
/// </summary>
internal sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private readonly RotateTransform _rotation = new();

    public Spinner()
    {
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);

        IsVisibleChanged += (_, _) => Sync();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        Loaded += (_, _) => Sync();
    }

    private void Sync()
    {
        if (IsVisible && IsLoaded && Motion.Enabled)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;

        if (Track is { } track)
        {
            var trackPen = new Pen(track, Thickness);
            trackPen.Freeze();
            dc.DrawEllipse(null, trackPen, centre, radius, radius);
        }

        // A quarter-and-a-bit of arc; the rotation does the rest.
        const double sweep = 100 * Math.PI / 180;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(sweep), centre.Y - radius * Math.Cos(sweep));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry { Figures = { figure } };
        geometry.Freeze();

        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
