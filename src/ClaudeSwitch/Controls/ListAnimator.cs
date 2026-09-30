using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeSwitch.Core;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace ClaudeSwitch.Controls;

/// <summary>
/// Makes the account list move instead of flicker.
///
/// The list is rebuilt from scratch on every refresh, so nothing in it can animate on its own.
/// This bridges the rebuild FLIP-style: <see cref="Capture"/> notes where every card sits, the
/// caller rebuilds, and <see cref="Play"/> starts each new card from its old spot and lets it
/// glide to its new one. Cards that did not exist before rise in; the very first population
/// staggers in; and the card that just became active gets a pop and a sweep of light, so a
/// switch is felt as well as seen.
///
/// Nothing moves while the window is hidden — positions there mean nothing.
/// </summary>
internal sealed class ListAnimator
{
    private readonly ItemsControl _list;
    private readonly Func<object, string> _keyOf;

    private Dictionary<string, double> _tops = [];
    private string? _activeKey;
    private bool _introPlayed;

    public ListAnimator(ItemsControl list, Func<object, string> keyOf)
    {
        _list = list;
        _keyOf = keyOf;
    }

    /// <summary>Records where every card is right now. Call just before the items change.</summary>
    public void Capture()
    {
        _tops = [];
        if (!_list.IsVisible) return;

        foreach (var item in _list.Items)
        {
            if (Container(item) is { IsVisible: true } container)
                _tops[_keyOf(item)] = Top(container);
        }
    }

    /// <summary>Animates the list from its captured state into the current one.</summary>
    public void Play(string? activeKey)
    {
        var previousActive = _activeKey;
        _activeKey = activeKey;

        if (!_list.IsVisible) return;
        if (!Motion.Enabled) { _introPlayed = true; return; }

        // Generate and place the new containers now, so every card can be moved back to where
        // it was before the first frame paints — otherwise each would flash at its new spot.
        _list.UpdateLayout();

        var index = 0;
        foreach (var item in _list.Items)
        {
            if (Container(item) is not { } card) continue;
            var key = _keyOf(item);

            if (!_introPlayed)
            {
                Motion.Rise(card, 18, Motion.Long, TimeSpan.FromMilliseconds(70 + 45 * index));
            }
            else if (_tops.TryGetValue(key, out var before))
            {
                var delta = before - Top(card);
                if (Math.Abs(delta) > 0.5)
                {
                    var (_, shift) = Motion.Transforms(card);
                    Motion.To(shift, System.Windows.Media.TranslateTransform.YProperty, 0,
                              Motion.Long, Motion.Enter, from: delta);
                }
            }
            else
            {
                Motion.Rise(card, 12, Motion.Long, fromScale: 0.97);
            }

            if (_introPlayed && key == activeKey && previousActive is not null && previousActive != activeKey)
                Celebrate(card);

            index++;
        }

        _introPlayed = true;
    }

    /// <summary>
    /// Draws the eye to one card — the account just added or signed back into — once whatever
    /// covered the list has gone.
    /// </summary>
    public void Highlight(string key)
    {
        if (!_list.IsVisible || !Motion.Enabled) return;

        foreach (var item in _list.Items)
        {
            if (_keyOf(item) != key || Container(item) is not { } card) continue;

            card.BringIntoView();
            Celebrate(card);
            return;
        }
    }

    /// <summary>Animates a card away, then runs <paramref name="then"/> — used before deleting.</summary>
    public void Remove(object item, Action then)
    {
        if (Container(item) is not { } card || !Motion.Enabled)
        {
            then();
            return;
        }

        var (scale, _) = Motion.Transforms(card);
        Motion.To(scale, System.Windows.Media.ScaleTransform.ScaleXProperty, 0.94, Motion.Short, Motion.Exit);
        Motion.To(scale, System.Windows.Media.ScaleTransform.ScaleYProperty, 0.94, Motion.Short, Motion.Exit);
        Motion.To(card, UIElement.OpacityProperty, 0, Motion.Short, Motion.Exit, done: then);
    }

    /// <summary>The newly active card: its avatar pops and a band of light crosses it.</summary>
    private static void Celebrate(ContentPresenter card)
    {
        if (Find(card, "AvatarHost") is { } avatar)
            Motion.Pop(avatar, 0.72, Motion.Long, TimeSpan.FromMilliseconds(80));

        if (Find(card, "Shine") is not Border shine) return;

        var color = Application.Current.TryFindResource("ShineColor") is Color c ? c : Colors.White;
        var clear = Color.FromArgb(0, color.R, color.G, color.B);
        var sweep = new TranslateTransform(-1, 0);

        shine.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = sweep,
            GradientStops =
            {
                new GradientStop(clear, 0.15),
                new GradientStop(color, 0.5),
                new GradientStop(clear, 0.85),
            },
        };
        shine.Opacity = 1;

        Motion.To(sweep, TranslateTransform.XProperty, 1.1, TimeSpan.FromMilliseconds(950), Motion.Standard,
                  from: -1.1, delay: TimeSpan.FromMilliseconds(120), done: () => shine.Opacity = 0);
    }

    private ContentPresenter? Container(object item) =>
        _list.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;

    private double Top(UIElement container) => container.TranslatePoint(new Point(0, 0), _list).Y;

    private static FrameworkElement? Find(ContentPresenter card, string name) =>
        card.ContentTemplate?.FindName(name, card) as FrameworkElement;
}
