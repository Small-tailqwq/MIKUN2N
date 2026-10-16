using System.Windows;
using System.Windows.Controls;
using MikuN2N.Models;
using MikuN2N.Services;

namespace MikuN2N;

public sealed class App : Application
{
    public ThemeManager ThemeManager { get; } = new();
}

public partial class MainWindow : Window
{
    private bool _connectionBusy = false;
    private bool _closeInProgress = false;
    private readonly SupernodeNode _connectionNode = new() { Name = "测试节点", Server = "node.invalid:3076", Community = "fixture-group" };
    internal int DisconnectRequests;
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += ConnectionLayout_SizeChanged;
        ShowActivated = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -20000;
        VersionBadgeText.Text = "v" + BuildIdentity.Version;
        NicknameBox.Text = "FixtureUser";
        NodeBox.ItemsSource = new[] { _connectionNode };
        NodeBox.SelectedItem = _connectionNode;
        VirtualIpText.Text = "192.0.2.55";
        PeerCountText.Text = "8"; DirectCountText.Text = "6"; UptimeText.Text = "00:12:34";
        PeersEmptyText.Visibility = Visibility.Collapsed;
        PeersGrid.ItemsSource = Enumerable.Range(1, 12).Select(i => new PeerSnapshot("peer-" + i, "测试朋友 " + i,
            "192.0.2." + (55 + i), 10 + i, DateTimeOffset.UtcNow, PeerConnectionMode.Direct)).ToList();
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        Advance(ConnectionState.Disconnected);
    }
    internal void Advance(ConnectionState state)
    {
        ConnectButton.Content = state is ConnectionState.Connected or ConnectionState.Reconnecting ? "断开连接" : state == ConnectionState.Connecting ? "取消连接" : "连接";
        StatusDetail.Text = state == ConnectionState.Connected ? "虚拟局域网已就绪。" : "正在等待连接状态。";
        StatusTitle.Text = state switch { ConnectionState.Connected => "已连接", ConnectionState.Reconnecting => "正在恢复连接", ConnectionState.Connecting => "正在连接", ConnectionState.Error => "连接异常", _ => "尚未连接" };
        NodeBox.IsEnabled = NicknameBox.IsEnabled = KeyBox.IsEnabled = state is ConnectionState.Disconnected or ConnectionState.Error;
        ApplyConnectionLayout(state);
    }
    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectRequests++;
        Advance(ConnectionState.Disconnected);
    }
}
