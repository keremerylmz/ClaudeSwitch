using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:Tr app.loading}"</c>.
///
/// It returns a binding rather than a string. Resolving to a plain string at load time looked
/// fine for the static chrome — which the window re-assigns by hand on a language change — but
/// WPF evaluates a markup extension inside a DataTemplate once and reuses the result for every
/// container it stamps out. The account cards therefore kept whatever language they were first
/// built in: switching to English left "Wechseln" and "5 Std." on every row until restart.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // WinForms is referenced for the tray icon, so Binding is ambiguous without this.
        var binding = new System.Windows.Data.Binding(nameof(LocString.Value))
        {
            Source = LocString.For(Key),
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// One observable string per translation key, so every <c>{loc:Tr}</c> in the app updates the
/// moment the language changes.
///
/// Instances are cached per key and shared. That is not just tidiness: the account list is
/// rebuilt on every refresh and every switch, so a fresh instance per binding would subscribe
/// to <see cref="Loc.Changed"/> again each time and leak steadily in an app designed to sit in
/// the tray for days.
/// </summary>
internal sealed class LocString : INotifyPropertyChanged
{
    private static readonly Dictionary<string, LocString> Cache = [];

    private readonly string _key;

    private LocString(string key)
    {
        _key = key;
        Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }

    public static LocString For(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var existing))
                Cache[key] = existing = new LocString(key);

            return existing;
        }
    }

    public string Value => Loc.T(_key);

    public event PropertyChangedEventHandler? PropertyChanged;
}
