using System.Windows;
using System.Windows.Media;

namespace ClaudeSwitch.Controls;

/// <summary>
/// Base for the usage visuals that ease toward their target instead of snapping to it.
///
/// <see cref="Percent"/> is the target — bind it to the live usage. A displayed value chases it a
/// fraction of the remaining distance each frame, so when a refresh moves 5-hour usage from 40 to
/// 55 the ring fills and the number ticks up rather than jumping. The per-frame hook is attached
/// only while there is distance left to cover, so an idle card costs nothing.
/// </summary>
internal abstract class AnimatedPercent : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(AnimatedPercent),
        new FrameworkPropertyMetadata(0.0, OnTargetChanged));

    /// <summary>The eased value, exposed so a number label can bind to it and tick up in step.</summary>
    public static readonly DependencyProperty ShownPercentProperty = DependencyProperty.Register(
        nameof(ShownPercent), typeof(double), typeof(AnimatedPercent),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public double ShownPercent { get => (double)GetValue(ShownPercentProperty); set => SetValue(ShownPercentProperty, value); }

    private bool _chasing;
    private bool _seeded;

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AnimatedPercent)d;

        // The first value a card ever gets should appear, not animate up from zero on every
        // rebuild of the list — only later changes are worth easing.
        if (!self._seeded)
        {
            self._seeded = true;
            self.ShownPercent = (double)e.NewValue;
            return;
        }

        self.StartChasing();
    }

    private void StartChasing()
    {
        if (_chasing) return;
        _chasing = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var target = Percent;
        var shown = ShownPercent;
        var delta = target - shown;

        if (Math.Abs(delta) < 0.3)
        {
            ShownPercent = target;
            CompositionTarget.Rendering -= OnFrame;
            _chasing = false;
            return;
        }

        // Frame-rate independence isn't worth a stopwatch here; 0.18 per frame settles in a few
        // hundred ms at 60fps, which reads as a deliberate ease rather than a snap.
        ShownPercent = shown + delta * 0.18;
    }
}
