using System.Windows;
using System.Windows.Input;

namespace ClaudeSwitch;

/// <summary>Single-line text prompt. Used for renaming a profile and for asking an email.</summary>
public partial class RenameDialog : Window
{
    public string NewName { get; private set; } = "";

    /// <summary>When true, an empty box is a valid answer (the caller treats it as "skip").</summary>
    private readonly bool _allowEmpty;

    public RenameDialog(string currentName, string? title = null, string? label = null, bool allowEmpty = false)
    {
        InitializeComponent();

        _allowEmpty = allowEmpty;
        if (title is not null) Title = title;
        if (label is not null) LabelText.Text = label;

        NameBox.Text = currentName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Commit();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
    }

    private void Commit()
    {
        var name = NameBox.Text.Trim();

        // An empty label would render as a blank row, so reject it unless the caller
        // explicitly allows it.
        if (name.Length == 0 && !_allowEmpty) return;

        NewName = name;
        DialogResult = true;
    }
}
