// Enabling both UseWPF and UseWindowsForms imports System.Windows AND System.Windows.Forms
// globally, and the two namespaces collide on several common type names. WinForms is only here
// for NotifyIcon (WPF has no tray API), so the names this app actually uses resolve to WPF.
// TrayIcon.cs reaches for its WinForms and System.Drawing types by their own names.

global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Button = System.Windows.Controls.Button;
global using Clipboard = System.Windows.Clipboard;
