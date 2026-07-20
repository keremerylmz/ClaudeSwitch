using System.Windows.Markup;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:Tr app.loading}"</c>.
///
/// Resolves at load time. That is enough because a language change recreates the window, so
/// every string is re-read for the new language when the window is rebuilt.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
