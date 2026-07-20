using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ClaudeSwitch.Core;

/// <summary>
/// Crossfades a change instead of letting it snap.
///
/// A snapshot of each window's current look is frozen on top as an adorner; the actual change
/// (theme swap, compact relayout) happens underneath instantly; then the snapshot fades out,
/// revealing the new state. The eye reads it as a smooth dissolve. Used for the dark-mode and
/// compact-mode toggles so they feel deliberate rather than abrupt.
/// </summary>
internal static class ThemeTransition
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(260);

    /// <summary>Snapshots <paramref name="windows"/>, runs <paramref name="swap"/>, then fades in the result.</summary>
    public static void Crossfade(IEnumerable<Window> windows, Action swap)
    {
        var overlays = new List<(AdornerLayer Layer, SnapshotAdorner Adorner)>();

        foreach (var window in windows)
        {
            if (window.Content is not UIElement root) continue;
            if (root is not FrameworkElement fe || fe.ActualWidth < 1 || fe.ActualHeight < 1) continue;

            var layer = AdornerLayer.GetAdornerLayer(root);
            if (layer is null) continue;

            var bitmap = Snapshot(fe);
            if (bitmap is null) continue;

            var adorner = new SnapshotAdorner(root, bitmap);
            layer.Add(adorner);
            overlays.Add((layer, adorner));
        }

        // Apply the change while every window is hidden behind its frozen snapshot.
        swap();

        foreach (var (layer, adorner) in overlays)
        {
            var fade = new DoubleAnimation(1, 0, Duration) { EasingFunction = new CubicEase() };
            fade.Completed += (_, _) => layer.Remove(adorner);
            adorner.BeginAnimation(UIElement.OpacityProperty, fade);
        }
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
            return null;   // no snapshot just means this window changes without the fade
        }
    }
}

/// <summary>Draws a frozen bitmap over the adorned element while the crossfade plays.</summary>
internal sealed class SnapshotAdorner : Adorner
{
    private readonly BitmapSource _bitmap;

    public SnapshotAdorner(UIElement adorned, BitmapSource bitmap) : base(adorned)
    {
        _bitmap = bitmap;
        IsHitTestVisible = false;   // brief, but never eat a click
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = AdornedElement.RenderSize;
        dc.DrawImage(_bitmap, new Rect(0, 0, size.Width, size.Height));
    }
}
