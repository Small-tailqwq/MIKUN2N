using System.Windows;
using MikuN2N.Services;
using Application = System.Windows.Application;

namespace MikuN2N;

public partial class ConflictingProcessDialog : Window
{
    public IReadOnlyList<ConflictingEdgeProcess> Processes { get; }

    public ConflictingProcessDialog(IReadOnlyList<ConflictingEdgeProcess> processes)
    {
        Processes = processes;
        InitializeComponent();
        DataContext = this;
        Icon = AppIconService.Icon;
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
