using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ClaudeSwitch.Core;

/// <summary>
/// One vocabulary for every transition in the app, so things move with the same character
/// wherever they are: entrances decelerate hard and settle softly, exits accelerate away, and
/// state changes ease on both ends.
///
/// Every helper honours Windows' "Show animations" switch. With it off, values jump straight to
/// their end state and completion callbacks still run — callers never need a second code path.
/// </summary>
internal static class Motion
{
    public static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan Short = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan Medium = TimeSpan.FromMilliseconds(340);
    public static readonly TimeSpan Long = TimeSpan.FromMilliseconds(520);

    /// <summary>Fast start, long gentle settle — for anything arriving on screen.</summary>
    public static readonly IEasingFunction Enter = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });

    /// <summary>Accelerates away — for anything leaving.</summary>
    public static readonly IEasingFunction Exit = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    /// <summary>Symmetric ease — for things changing in place.</summary>
    public static readonly IEasingFunction Standard = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>A small overshoot — for pops that should feel physical.</summary>
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });

    /// <summary>False when the user turned animations off in Windows (Accessibility → Visual effects).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// The animation currently driving each (object, property) pair. A superseded animation's
    /// Completed event must not run its callback: fading a layer out and straight back in would
    /// otherwise collapse it the moment the stale fade-out "finished".
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        DependencyObject, Dictionary<DependencyProperty, AnimationTimeline>> Current = new();

    private static void Track(DependencyObject target, DependencyProperty property, AnimationTimeline? animation)
    {
        var map = Current.GetOrCreateValue(target);
        if (animation is null) map.Remove(property);
        else map[property] = animation;
    }

    private static bool IsCurrent(DependencyObject target, DependencyProperty property, AnimationTimeline animation) =>
        Current.TryGetValue(target, out var map) &&
        map.TryGetValue(property, out var live) && ReferenceEquals(live, animation);

    /// <summary>
    /// Animates <paramref name="property"/> on <paramref name="target"/> to <paramref name="to"/>.
    ///
    /// When it finishes, the final value is written as the property's own value and the animation
    /// is removed. Leaving a held animation in place would silently override every later direct
    /// assignment — the classic "why won't this opacity change" trap.
    /// </summary>
    public static void To(
        DependencyObject target, DependencyProperty property, double to, TimeSpan duration,
        IEasingFunction? ease = null, double? from = null, TimeSpan? delay = null, Action? done = null)
    {
        if (target is not IAnimatable animatable || !Enabled || duration <= TimeSpan.Zero)
        {
            Set(target, property, to);
            done?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(to, duration)
        {
            EasingFunction = ease ?? Standard,
            BeginTime = delay ?? TimeSpan.Zero,
        };
        if (from is { } start) animation.From = start;

        animation.Completed += (_, _) =>
        {
            if (!IsCurrent(target, property, animation)) return;

            Track(target, property, null);
            target.SetValue(property, to);
            animatable.BeginAnimation(property, null);
            done?.Invoke();
        };

        Track(target, property, animation);
        animatable.BeginAnimation(property, animation);
    }

    /// <summary>Stops any animation on <paramref name="property"/> and pins it to <paramref name="value"/>.</summary>
    public static void Set(DependencyObject target, DependencyProperty property, double value)
    {
        Track(target, property, null);
        (target as IAnimatable)?.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    // ── transforms ──────────────────────────────────────────────────────────

    /// <summary>
    /// A scale-then-translate transform on <paramref name="element"/>, created on first use and
    /// reused afterwards so repeated animations never stack transforms.
    /// </summary>
    public static (ScaleTransform Scale, TranslateTransform Shift) Transforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup { Children.Count: 2 } group &&
            group.Children[0] is ScaleTransform s && group.Children[1] is TranslateTransform t &&
            !group.IsFrozen)
            return (s, t);

        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, shift);
    }

    /// <summary>Fades and lifts an element into place — the standard entrance.</summary>
    public static void Rise(UIElement element, double distance = 12, TimeSpan? duration = null,
                            TimeSpan? delay = null, double fromScale = 1, Action? done = null)
    {
        var d = duration ?? Long;
        var (scale, shift) = Transforms(element);

        element.Opacity = 0;
        shift.Y = distance;
        scale.ScaleX = scale.ScaleY = fromScale;

        To(element, UIElement.OpacityProperty, 1, d, Enter, delay: delay, done: done);
        To(shift, TranslateTransform.YProperty, 0, d, Enter, delay: delay);
        if (Math.Abs(fromScale - 1) > 0.0001)
        {
            To(scale, ScaleTransform.ScaleXProperty, 1, d, Enter, delay: delay);
            To(scale, ScaleTransform.ScaleYProperty, 1, d, Enter, delay: delay);
        }
    }

    /// <summary>A quick overshooting scale pop, from <paramref name="from"/> back to full size.</summary>
    public static void Pop(UIElement element, double from = 0.82, TimeSpan? duration = null, TimeSpan? delay = null)
    {
        var d = duration ?? Long;
        var (scale, _) = Transforms(element);
        To(scale, ScaleTransform.ScaleXProperty, 1, d, Spring, from, delay);
        To(scale, ScaleTransform.ScaleYProperty, 1, d, Spring, from, delay);
    }

    /// <summary>A short horizontal shake — the universal "that didn't work".</summary>
    public static void Shake(UIElement element)
    {
        if (!Enabled) return;

        var (_, shift) = Transforms(element);
        var frames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] offsets = [0, -7, 6, -4, 3, -1.5, 0];
        for (var i = 0; i < offsets.Length; i++)
        {
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i],
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420.0 * i / (offsets.Length - 1))),
                Standard));
        }
        frames.Completed += (_, _) => { shift.X = 0; shift.BeginAnimation(TranslateTransform.XProperty, null); };
        shift.BeginAnimation(TranslateTransform.XProperty, frames);
    }

    // ── layout ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="change"/> — anything that alters the element's natural height — and
    /// glides the height from the old value to the new one instead of letting it jump. WPF cannot
    /// animate to "Auto", so the new height is measured up front and released once the glide ends.
    /// </summary>
    public static void MorphHeight(FrameworkElement element, Action change, TimeSpan? duration = null)
    {
        var before = element.ActualHeight;
        change();

        // Release any glide in progress so the measurement below sees the natural height.
        Track(element, FrameworkElement.HeightProperty, null);
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.Height = double.NaN;

        if (!Enabled || before <= 0 || !element.IsVisible) return;

        element.Measure(new Size(element.ActualWidth > 0 ? element.ActualWidth : double.PositiveInfinity,
                                 double.PositiveInfinity));
        var after = element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom;
        if (after <= 0 || Math.Abs(after - before) < 1) return;

        var animation = new DoubleAnimation(before, after, duration ?? Medium) { EasingFunction = Enter };
        animation.Completed += (_, _) =>
        {
            if (!IsCurrent(element, FrameworkElement.HeightProperty, animation)) return;

            Track(element, FrameworkElement.HeightProperty, null);
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = double.NaN;
        };

        Track(element, FrameworkElement.HeightProperty, animation);
        element.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }

    /// <summary>Crossfades from one panel to another that occupy the same slot.</summary>
    public static void Swap(UIElement? from, UIElement to, double lift = 8)
    {
        if (from is not null && !ReferenceEquals(from, to))
        {
            Set(from, UIElement.OpacityProperty, 0);
            from.Visibility = Visibility.Collapsed;
        }

        to.Visibility = Visibility.Visible;
        Rise(to, lift, Medium);
    }
}
