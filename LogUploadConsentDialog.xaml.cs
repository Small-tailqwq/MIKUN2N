using System.Windows;
using MikuN2N.Services;
using MikuN2N.Models;
using Application = System.Windows.Application;

namespace MikuN2N;

public partial class LogUploadConsentDialog : Window
{
    public LogUploadConsentDialog(TestBuildProfile profile, bool persistent = false, SupernodeNode? node = null)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        DestinationText.Text = $"接收服务器：{new Uri(profile.UploadUrl).Authority}" +
            (node is null ? string.Empty : $"\n当前节点：{node.Name}（{node.Server}）");
        if (persistent)
        {
            ConsentTitleText.Text = "始终允许向此服务器上传诊断日志？";
            ConsentPolicyText.Text = "仅授权当前节点向此接收地址及证书上传新日志。编辑或切换节点、更换接收地址或证书后须重新授权，不会补传过去拒绝上传的记录。服务器最多保留 24 小时；可随时在设置中撤销授权或在主窗口停止上传。";
            AllowButton.Content = "同意并保存始终允许";
        }
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
