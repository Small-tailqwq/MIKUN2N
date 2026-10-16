using System.Windows;
using MikuN2N.Models;

namespace MikuN2N;

public partial class MainWindow
{
    private bool _hasConnectedLayout;
    private bool _connectionDetailsExpanded = true;

    private void ApplyConnectionLayout(ConnectionState state)
    {
        if (state == ConnectionState.Connected && !_hasConnectedLayout)
        {
            _hasConnectedLayout = true;
            _connectionDetailsExpanded = false;
        }
        else if (state is ConnectionState.Disconnected or ConnectionState.Error)
        {
            _hasConnectedLayout = false;
            _connectionDetailsExpanded = true;
        }
        // Connecting/reconnecting updates preserve a successful session's layout,
        // including a deliberate manual expansion, until an error or disconnect.
        RefreshConnectionLayout();
    }

    private void ConnectionDetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _connectionDetailsExpanded = !_connectionDetailsExpanded;
        RefreshConnectionLayout();
    }

    private void RefreshConnectionLayout()
    {
        ConnectionConfigurationCard.Visibility = _connectionDetailsExpanded ? Visibility.Visible : Visibility.Collapsed;
        ConnectionActionsPanel.Visibility = _hasConnectedLayout ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Visibility = _hasConnectedLayout ? Visibility.Collapsed : Visibility.Visible;
        ConnectionDetailsToggle.Content = _connectionDetailsExpanded ? "收起详情" : "展开详情";
        ConnectionDetailsHintText.Text = _connectionDetailsExpanded ? "连接配置" : "连接配置已收起";
        RefreshConnectionActions();
        RefreshConnectionIdentity();
        UpdatePeerListHeight();
    }

    private void RefreshConnectionActions()
    {
        CompactDisconnectButton.Content = ConnectButton.Content;
        CompactDisconnectButton.IsEnabled = ConnectButton.IsEnabled && !_connectionBusy && !_closeInProgress;
    }

    private void RefreshConnectionIdentity()
    {
        var selected = NodeBox.SelectedItem as SupernodeNode;
        var node = _hasConnectedLayout ? _connectionNode ?? selected : selected;
        RoomNameText.Text = $"小组：{(string.IsNullOrWhiteSpace(node?.Community) ? "—" : node.Community)}";
        var nickname = NicknameBox.Text.Trim();
        ConnectionIdentityText.Text = $"节点：{node?.Name ?? "—"} · 昵称：{(nickname.Length == 0 ? "—" : nickname)}";
        ConnectionIdentityText.Visibility = _hasConnectedLayout ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConnectionLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePeerListHeight();

    private void UpdatePeerListHeight()
    {
        var compact = _hasConnectedLayout && !_connectionDetailsExpanded;
        PeersGrid.MaxHeight = compact ? Math.Clamp(ActualHeight - 440, 150, 440) : 228;
        PeersGrid.MinHeight = compact ? Math.Min(240, PeersGrid.MaxHeight) : 88;
    }
}
