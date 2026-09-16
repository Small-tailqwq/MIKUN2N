using System.Windows;
using MikuN2N.Services;
using Application = System.Windows.Application;

namespace MikuN2N;

public partial class LogUploadConsentDialog : Window
{
    public LogUploadConsentDialog(TestBuildProfile profile)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        DestinationText.Text = $"接收服务器：{new Uri(profile.UploadUrl).Authority}";
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
