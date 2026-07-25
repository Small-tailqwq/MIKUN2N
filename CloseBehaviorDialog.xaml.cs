using System.Windows;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;

namespace MikuN2N;

public partial class CloseBehaviorDialog : Window
{
    public ClosePreference Selection { get; private set; } = ClosePreference.Ask;

    public CloseBehaviorDialog()
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        Selection = ClosePreference.MinimizeToTray;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Selection = ClosePreference.Exit;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
