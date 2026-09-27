using System.Windows;
using System.Windows.Media;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace MikuN2N;

public enum MessageKind
{
    Info,
    Warning,
    Error,
    Question
}

public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel
}

/// <summary>
/// Replaces the system MessageBox, which ignores the app palette and pops a white box
/// over the dark theme. The process-level crash and second-instance notices keep the
/// system box because they can appear before any theme is loaded.
/// </summary>
public partial class ThemedMessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    private ThemedMessageDialog(string title, string message, MessageKind kind)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        HeadingText.Text = title;
        MessageText.Text = message;
        (IconGlyph.Text, var brush) = kind switch
        {
            MessageKind.Warning => ("!", "Warning"),
            MessageKind.Error => ("×", "Danger"),
            MessageKind.Question => ("?", "Accent"),
            _ => ("i", "Accent")
        };
        IconBadge.SetResourceReference(BackgroundProperty, brush);
        // White on the bright dark-theme accent is hard to read; match the primary button.
        if (brush == "Accent")
        {
            IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x2E));
        }
    }

    public static void Show(Window? owner, string message, string title, MessageKind kind = MessageKind.Info)
    {
        var dialog = Create(owner, title, message, kind, "知道了");
        // A single button must also answer Esc.
        dialog.ConfirmButton.IsCancel = true;
        dialog.ShowDialog();
    }

    public static bool Confirm(Window? owner, string message, string title, string confirmText = "确定",
        string cancelText = "取消", MessageKind kind = MessageKind.Question)
    {
        var dialog = Create(owner, title, message, kind, confirmText);
        dialog.CancelButton.Content = cancelText;
        dialog.CancelButton.Visibility = Visibility.Visible;
        dialog.ShowDialog();
        return dialog._choice == DialogChoice.Primary;
    }

    public static DialogChoice Choose(Window? owner, string message, string title, string primaryText,
        string secondaryText, string cancelText = "取消", MessageKind kind = MessageKind.Question)
    {
        var dialog = Create(owner, title, message, kind, primaryText);
        dialog.SecondaryButton.Content = secondaryText;
        dialog.SecondaryButton.Visibility = Visibility.Visible;
        dialog.CancelButton.Content = cancelText;
        dialog.CancelButton.Visibility = Visibility.Visible;
        dialog.ShowDialog();
        return dialog._choice;
    }

    private static ThemedMessageDialog Create(Window? owner, string title, string message, MessageKind kind,
        string confirmText)
    {
        var dialog = new ThemedMessageDialog(title, message, kind);
        dialog.ConfirmButton.Content = confirmText;
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Primary;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Secondary;
        DialogResult = false;
    }
}
