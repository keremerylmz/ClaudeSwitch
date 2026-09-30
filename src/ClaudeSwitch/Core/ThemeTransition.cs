using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace ClaudeSwitch.Core;

/// <summary>
/// Turns a change that would snap into one that dissolves or spreads.
///
/// A snapshot of each window's current look is frozen on top as an adorner; the actual change
/// (theme swap, compact relayout) happens underneath instantly; then the snapshot is taken
/// away — faded out for a crossfade, or eaten by a growing circle for a reveal, so the new
/// theme appears to spread outward from the control that asked for it.
/// </summary>
internal static class ThemeTransition
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(620);

    /// <summary>Snapshots <paramref name="windows"/>, runs <paramref name="swap"/>, then fades in the result.</summary>
    public static void Crossfade(IEnumerable<Window> windows, Action swap)
    {
        if (!Motion.Enabled) { swap(); return; }

        var overlays = windows.Select(Cover).Where(o => o is not null).ToList();

        // Apply the change while every window is hidden behind its frozen snapshot.
        swap();

        foreach (var overlay in overlays) FadeAway(overlay!.Value);
    }

    /// <summary>
    /// Runs <paramref name="swap"/> and reveals the result in <paramref name="window"/> as a
    /// circle growing from <paramref name="origin"/> (in the window's content coordinates). Every
    /// other visible window crossfades at the same time.
    /// </summary>
    public static void Reveal(Window window, Point origin, Action swap)
    {
        if (!Motion.Enabled) { swap(); return; }

        var main = Cover(window);
        var others = Application.Current.Windows.Cast<Window>()
            .Where(w => w.IsVisible && !ReferenceEquals(w, window))
            .Select(Cover)
            .Where(o => o is not null)
            .ToList();

        swap();

        foreach (var overlay in others) FadeAway(overlay!.Value);

        if (main is not { } cover) return;

        // Far enough to clear the corner furthest from the click.
        var size = cover.Adorner.AdornedElement.RenderSize;
        var reach = new[]
        {
            new Point(0, 0), new Point(size.Width, 0),
            new Point(0, size.Height), new Point(size.Width, size.Height),
        }.Max(corner => (corner - origin).Length);

        cover.Adorner.HoleCenter = origin;

        var grow = new DoubleAnimation(0, reach + 2, RevealDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        grow.Completed += (_, _) => cover.Layer.Remove(cover.Adorner);
        cover.Adorner.BeginAnimation(SnapshotAdorner.HoleRadiusProperty, grow);
    }

    private static (AdornerLayer Layer, SnapshotAdorner Adorner)? Cover(Window window)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
            return null;

        var layer = AdornerLayer.GetAdornerLayer(root);
        if (layer is null) return null;

        var bitmap = Snapshot(root);
        if (bitmap is null) return null;

        var adorner = new SnapshotAdorner(root, bitmap);
        layer.Add(adorner);
        return (layer, adorner);
    }

    private static void FadeAway((AdornerLayer Layer, SnapshotAdorner Adorner) overlay)
    {
        var fade = new DoubleAnimation(1, 0, FadeDuration) { EasingFunction = new CubicEase() };
        fade.Completed += (_, _) => overlay.Layer.Remove(overlay.Adorner);
        overlay.Adorner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static BitmapSource? Snapshot(FrameworkElement element)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var w = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
            if (w < 1 || h < 1) return null;

            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(element);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
        {
            return null;   // no snapshot just means this window changes without the transition
        }
    }
}

/// <summary>
/// Draws a frozen bitmap over the adorned element while a transition plays — optionally with a
/// circular hole cut out of it, through which the new state shows.
/// </summary>
internal sealed class SnapshotAdorner : Adorner
{
    public static readonly DependencyProperty HoleRadiusProperty = DependencyProperty.Register(
        nameof(HoleRadius), typeof(double), typeof(SnapshotAdorner),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly BitmapSource _bitmap;

    public SnapshotAdorner(UIElement adorned, BitmapSource bitmap) : base(adorned)
    {
        _bitmap = bitmap;
        IsHitTestVisible = false;   // brief, but never eat a click
    }

    public double HoleRadius
    {
        get => (double)GetValue(HoleRadiusProperty);
        set => SetValue(HoleRadiusProperty, value);
    }

    public Point HoleCenter { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = AdornedElement.RenderSize;
        var bounds = new Rect(0, 0, size.Width, size.Height);

        var radius = HoleRadius;
        if (radius <= 0)
        {
            dc.DrawImage(_bitmap, bounds);
            return;
        }

        var visible = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(bounds),
            new EllipseGeometry(HoleCenter, radius, radius));

        dc.PushClip(visible);
        dc.DrawImage(_bitmap, bounds);
        dc.Pop();
    }
}
