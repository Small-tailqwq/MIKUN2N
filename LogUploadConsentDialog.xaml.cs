using System.Windows;
using MikuN2N.Services;
using MikuN2N.Models;
using Application = System.Windows.Application;

namespace MikuN2N;

public partial class LogUploadConsentDialog : Window
{
    public LogUploadConsentDialog(TestBuildProfile profile, bool persistent = false, SupernodeNode? node = null,
        bool certificateChanged = false)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        var authority = new Uri(profile.UploadUrl).Authority;
        if (profile.IsNodeReceiver)
        {
            var fingerprint = profile.CertificateSha256.ToUpperInvariant();
            ConsentTitleText.Text = "允许把本次联机的诊断日志上传到节点服务器吗？";
            DestinationText.Text = $"接收服务器：{authority}，即当前节点「{node?.Name}」所用的服务器。\n" +
                $"证书指纹：{fingerprint[..16]}…{fingerprint[^8..]}\n" +
                "日志会交给这台服务器的管理员查看。只有在你信任该节点服务器及其管理员时才应同意。";
            CertificateWarningText.Visibility = certificateChanged ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            DestinationText.Text = $"接收服务器：{authority}" +
                (node is null ? string.Empty : $"\n当前节点：{node.Name}（{node.Server}）");
        }
        if (persistent)
        {
            ConsentTitleText.Text = "始终允许向此服务器上传诊断日志？";
            ConsentPolicyText.Text = "仅授权当前节点向此接收地址及证书上传新日志。编辑或切换节点、更换接收地址或证书后须重新授权，不会补传过去拒绝上传的记录。保留期由 supernode 服务提供商设置，客户端不能保证删除时间；可随时在设置中撤销授权或在主窗口停止上传。";
            AllowButton.Content = "同意并保存始终允许";
        }
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
