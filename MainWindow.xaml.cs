using System.Net;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace MikuN2N;

public partial class MainWindow : Window
{
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(170, 177, 192));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(67, 132, 235));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(230, 158, 52));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(44, 180, 125));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(226, 82, 89));
    private static readonly Brush Nat1 = new SolidColorBrush(Color.FromRgb(44, 180, 125));
    private static readonly Brush Nat2 = new SolidColorBrush(Color.FromRgb(53, 184, 197));
    private static readonly Brush Nat12 = new SolidColorBrush(Color.FromRgb(42, 177, 158));
    private static readonly Brush Nat3 = new SolidColorBrush(Color.FromRgb(230, 158, 52));
    private static readonly Brush Nat4 = new SolidColorBrush(Color.FromRgb(226, 82, 89));
    private static readonly Brush Nat1Soft = new SolidColorBrush(Color.FromArgb(42, 44, 180, 125));
    private static readonly Brush Nat2Soft = new SolidColorBrush(Color.FromArgb(42, 53, 184, 197));
    private static readonly Brush Nat12Soft = new SolidColorBrush(Color.FromArgb(42, 42, 177, 158));
    private static readonly Brush Nat3Soft = new SolidColorBrush(Color.FromArgb(42, 230, 158, 52));
    private static readonly Brush Nat4Soft = new SolidColorBrush(Color.FromArgb(42, 226, 82, 89));

    private readonly SettingsStore _settingsStore = new();
    private readonly EdgeController _edgeController = new();
    private readonly TrayIconService _trayIcon;
    private readonly EasterEggManager _easterEggs;
    private readonly EasterEggVisualController _easterEggVisuals;
    private AppSettings _settings;
    private ConnectionSnapshot _lastSnapshot = new(
        ConnectionState.Disconnected,
        "尚未连接",
        "填写昵称后点击连接，即可加入朋友们的虚拟局域网。");
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _shownTrayTip;
    private bool _connectionBusy;
    private TestDiagnosticsSession? _diagnostics;
    private readonly ObservableCollection<PeerSnapshot> _displayPeers = [];
    private ContextMenu? _activePeerMenu;
    private string? _activePeerMenuNodeId;
    private PeerConnectionMode _activePeerMenuMode;

    public MainWindow()
    {
        InitializeComponent();
        PeersGrid.ItemsSource = _displayPeers;
        Icon = AppIconService.Icon;
        _easterEggs = ((App)Application.Current).EasterEggs;
        _easterEggVisuals = new EasterEggVisualController(this, EasterEggOverlay, _easterEggs);
        _easterEggs.JackpotActivated += EasterEggs_JackpotActivated;
        Closed += (_, _) => _easterEggs.JackpotActivated -= EasterEggs_JackpotActivated;
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        VersionBadgeText.Text = $"v{BuildIdentity.Version}";
        VersionBadgeText.ToolTip = $"MikuN2N 构建版本：{BuildIdentity.Version}";
        _settings = _settingsStore.Load();
        if (!Guid.TryParseExact(_settings.NodeId, "N", out _))
        {
            _settings.NodeId = Guid.NewGuid().ToString("N");
        }
        NicknameBox.Text = _settings.Nickname;
        RememberKeyBox.IsChecked = _settings.RememberKey;
        KeyBox.Password = _settingsStore.LoadKey(_settings);
        if (TestBuildProfile.Current is not null)
        {
            DiagnosticsPanel.Visibility = Visibility.Visible;
            DiagnosticsStatusText.Text = "连接前会询问日志上传授权";
        }
        RefreshNodeBox();

        _edgeController.SnapshotChanged += EdgeController_SnapshotChanged;
        _edgeController.LogReceived += EdgeController_LogReceived;
        _trayIcon = new TrayIconService();
        _trayIcon.ShowRequested += (_, _) => Dispatcher.InvokeAsync(ShowFromTray);
        _trayIcon.SettingsRequested += (_, _) => Dispatcher.InvokeAsync(() => OpenSettings());
        _trayIcon.ConnectionRequested += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            ShowFromTray();
            ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        });
        _trayIcon.ExitRequested += (_, _) => Dispatcher.InvokeAsync(ExitApplicationAsync);
        _trayIcon.Update(_lastSnapshot, false);
        Closing += MainWindow_Closing;
        UpdateTapAvailability();
    }

    private void PeersGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var innerScroll = FindVisualChild<ScrollViewer>(PeersGrid);
        if (innerScroll is null)
        {
            return;
        }

        var atTop = innerScroll.VerticalOffset <= 0;
        var atBottom = innerScroll.VerticalOffset >= innerScroll.ScrollableHeight;

        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(PeersGrid) as UIElement;
            if (parent is not null)
            {
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                });
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void NodeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodeBox.SelectedItem is SupernodeNode node)
        {
            _settings.ActiveNodeId = node.Id;
        }
        RefreshNodeSummary();
    }

    private void ManageNodes_Click(object sender, RoutedEventArgs e) => OpenSettings(NodesTabIndex);

    /// <summary>Reloads the node selector from settings; also used after the user edits nodes.</summary>
    private void RefreshNodeBox()
    {
        NodeBox.SelectionChanged -= NodeBox_SelectionChanged;
        NodeBox.ItemsSource = null;
        NodeBox.ItemsSource = new ObservableCollection<SupernodeNode>(_settings.Nodes);
        var active = _settings.ActiveNode;
        NodeBox.SelectedItem = active;
        if (active is not null)
        {
            _settings.ActiveNodeId = active.Id;
        }
        NodeBox.SelectionChanged += NodeBox_SelectionChanged;
        RefreshNodeSummary();
    }

    private void RefreshNodeSummary()
    {
        if (NodeBox is null || RoomNameText is null)
        {
            return;
        }

        var node = NodeBox.SelectedItem as SupernodeNode;
        NodeAddressText.Text = node is null ? "尚未添加节点" : node.Server;
        NodeCommunityText.Text = node is null ? "—" : node.Community;
        RoomNameText.Text = $"房间：{(string.IsNullOrWhiteSpace(node?.Community) ? "—" : node.Community)}";
        ConnectButton.Content = ConnectButtonLabel();
    }

    private string ConnectButtonLabel()
    {
        var community = (NodeBox?.SelectedItem as SupernodeNode)?.Community;
        return string.IsNullOrWhiteSpace(community) ? "连接" : $"连接到 {community}";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy) return;
        if (_edgeController.IsRunning)
        {
            _connectionBusy = true;
            try
            {
                ConnectButton.IsEnabled = false;
                await _edgeController.StopAsync();
                await FinishDiagnosticsAsync();
                SetInputsEnabled(true);
                ConnectButton.Content = "连接";
            }
            finally
            {
                _connectionBusy = false;
                ConnectButton.IsEnabled = true;
            }
            return;
        }

        if (!TryValidate(out var error))
        {
            MessageBox.Show(this, error, "请检查连接信息", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var conflictingProcesses = EdgeController.FindConflictingEdgeProcesses();
        if (conflictingProcesses.Count > 0)
        {
            var confirmation = new ConflictingProcessDialog(conflictingProcesses)
            {
                Owner = this
            };
            if (confirmation.ShowDialog() != true)
            {
                return;
            }

            ConnectButton.IsEnabled = false;
            try
            {
                await EdgeController.TerminateConflictingEdgeProcessesAsync(
                    conflictingProcesses.Select(process => process.ProcessId).ToArray());
                LogBox.AppendText(
                    $"[{DateTime.Now:HH:mm:ss}] 已结束 {conflictingProcesses.Count} 个确认的旧 n2n/n3n 进程。{Environment.NewLine}");
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "无法清理旧进程",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        SaveSettings();
        LogBox.AppendText($"{Environment.NewLine}===== 开始新的连接 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
        var node = _settings.ActiveNode ?? NodeBox.SelectedItem as SupernodeNode;
        if (node is null)
        {
            SetInputsEnabled(true);
            ConnectButton.Content = "重新连接";
            ApplySnapshot(new ConnectionSnapshot(
                ConnectionState.Error,
                "尚未添加节点",
                "请先在“管理节点”里添加一个自建或朋友分享的 supernode 地址。"));
            return;
        }

        SetInputsEnabled(false);
        ConnectButton.Content = "取消连接";

        try
        {
            _connectionBusy = true;
            await FinishDiagnosticsAsync();
            if (TestBuildProfile.Current is { } profile)
            {
                var consent = new LogUploadConsentDialog(profile) { Owner = this }.ShowDialog() == true;
                if (_closeInProgress) return;
                try
                {
                    _diagnostics = new TestDiagnosticsSession(profile, consent, KeyBox.Password,
                        status => Dispatcher.InvokeAsync(() => DiagnosticsStatusText.Text = status));
                    StopUploadButton.IsEnabled = consent;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    DiagnosticsStatusText.Text = "详细日志无法写入，本次不会上传日志。";
                }
            }
            await _edgeController.StartAsync(
                node.Server,
                node.Community,
                NicknameBox.Text.Trim(),
                _settings.NodeId,
                KeyBox.Password,
                node.Name,
                experimentalIpv6P2p: _settings.ExperimentalIpv6P2p,
                diagnostics: _diagnostics);
        }
        catch (Exception exception)
        {
            _diagnostics?.Write("connection_start_failed", new { error = exception.GetType().Name });
            await FinishDiagnosticsAsync();
            SetInputsEnabled(true);
            ConnectButton.Content = "重新连接";
            ApplySnapshot(new ConnectionSnapshot(
                ConnectionState.Error,
                "无法开始连接",
                exception.Message));
        }
        finally
        {
            _connectionBusy = false;
        }
    }

    private async void StopUpload_Click(object sender, RoutedEventArgs e)
    {
        StopUploadButton.IsEnabled = false;
        if (_diagnostics is { } diagnostics)
            await diagnostics.StopUploadAsync();
    }

    private async Task FinishDiagnosticsAsync()
    {
        var diagnostics = _diagnostics;
        _diagnostics = null;
        StopUploadButton.IsEnabled = false;
        if (diagnostics is not null)
            await diagnostics.DisposeAsync();
    }

    private bool TryValidate(out string error)
    {
        if (!EdgeController.HasTapAdapter())
        {
            TapWarningBorder.Visibility = Visibility.Visible;
            error = "尚未安装 n2n 所需的虚拟网卡。请先点击“安装网络组件”，安装完成后再连接。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(NicknameBox.Text))
        {
            error = "请填写一个昵称，朋友们会用它辨认你。";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(NicknameBox.Text.Trim()) > 31)
        {
            error = "昵称太长，请缩短到 31 个英文字符或大约 10 个汉字以内。";
            return false;
        }
        var node = _settings.ActiveNode ?? NodeBox.SelectedItem as SupernodeNode;
        if (node is null || !TryParseServer(node.Server))
        {
            error = "当前节点还没有可用的服务器地址。请打开“管理节点”，填写形如 vps.example.com:3076 的地址。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(node.Community) ||
            Encoding.UTF8.GetByteCount(node.Community.Trim()) > 20 ||
            node.Community.Any(char.IsWhiteSpace))
        {
            error = "小组名称不能为空、不能包含空格，且最长为 20 个英文字符；请打开“管理节点”修改当前节点。";
            return false;
        }
        if (KeyBox.Password.Any(character => character > 127))
        {
            error = "n2n 联机密钥只能使用英文、数字和常见英文符号。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async void InstallTapButton_Click(object sender, RoutedEventArgs e)
    {
        var installerPath = Path.Combine(AppContext.BaseDirectory, "Runtime", "tap-windows-installer.exe");
        if (!File.Exists(installerPath))
        {
            MessageBox.Show(this, "程序包中缺少 TAP-Windows 安装器。", "程序包不完整",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        InstallTapButton.IsEnabled = false;
        TapStatusText.Text = "安装程序正在运行，请按提示完成安装。";
        try
        {
            using var installer = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            if (installer is not null)
            {
                await installer.WaitForExitAsync();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法启动安装程序：{exception.Message}", "安装失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallTapButton.IsEnabled = true;
            UpdateTapAvailability();
        }
    }

    private void UpdateTapAvailability()
    {
        var installed = EdgeController.HasTapAdapter();
        TapWarningBorder.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        TapStatusText.Text = installed
            ? "虚拟网卡已就绪。"
            : "首次使用请先安装，完成后就不需要重复操作。";
    }

    private static bool TryParseServer(string value)
    {
        var servers = EdgeController.SplitServers(value);
        return servers.Count > 0 && servers.All(TryParseServerToken);
    }

    private static bool TryParseServerToken(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > 65535)
        {
            return false;
        }
        var host = value[..separator];
        return IPAddress.TryParse(host, out _) ||
               Uri.CheckHostName(host) is UriHostNameType.Dns;
    }

    private void SaveSettings()
    {
        _settings = new AppSettings
        {
            Nodes = _settings.Nodes,
            ActiveNodeId = _settings.ActiveNodeId,
            Nickname = NicknameBox.Text.Trim(),
            NodeId = _settings.NodeId,
            RememberKey = RememberKeyBox.IsChecked == true,
            Theme = _settings.Theme,
            CloseBehavior = _settings.CloseBehavior,
            LogRetentionDays = _settings.LogRetentionDays,
            ExperimentalIpv6P2p = _settings.ExperimentalIpv6P2p
        };
        _settingsStore.Save(_settings, KeyBox.Password);
    }

    private void EdgeController_SnapshotChanged(object? sender, ConnectionSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
    }

    private void EdgeController_LogReceived(object? sender, string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void ApplySnapshot(ConnectionSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _trayIcon.Update(snapshot, _edgeController.IsRunning);
        StatusTitle.Text = snapshot.Summary;
        StatusDetail.Text = snapshot.Detail;
        VirtualIpText.Text = snapshot.VirtualIp;
        PeerCountText.Text = snapshot.State == ConnectionState.Connected && snapshot.PeerCount >= 0
            ? snapshot.PeerCount.ToString()
            : "—";
        DirectCountText.Text = snapshot.State == ConnectionState.Connected && snapshot.DirectPeerCount >= 0
            ? snapshot.DirectPeerCount.ToString()
            : "—";
        UpdateNatIndicator(snapshot);
        var showSupernode = snapshot.State == ConnectionState.Connected &&
                            snapshot.SupernodeText != "—";
        SupernodeBadge.Visibility = showSupernode ? Visibility.Visible : Visibility.Collapsed;
        SupernodeText.Text = $"中继：{snapshot.SupernodeText}";
        UptimeText.Text = snapshot.Uptime is { } uptime
            ? $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}"
            : "—";
        var displayPeers = snapshot.Peers?.ToList() ?? [];
        if (_easterEggs.IsJackpot && displayPeers.All(peer => peer.NodeId != "mikun2n-easter-isaac"))
        {
            displayPeers.Insert(0, new PeerSnapshot(
                "mikun2n-easter-isaac",
                "Isaac",
                string.Empty,
                _easterEggs.IsaacLatency,
                DateTimeOffset.Now,
                PeerConnectionMode.LanDirect));
        }
        CloseStalePeerMenu(displayPeers);
        SynchronizeDisplayedPeers(displayPeers);
        PeersEmptyText.Visibility = displayPeers.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        (StatusIndicator.Background, StatusGlyph.Text) = snapshot.State switch
        {
            ConnectionState.Connecting => (Blue, "…"),
            ConnectionState.Reconnecting => (Amber, "↻"),
            ConnectionState.Connected => (Green, "✓"),
            ConnectionState.Error => (Red, "!"),
            _ => (Gray, "○")
        };

        if (snapshot.State == ConnectionState.Reconnecting)
        {
            ConnectButton.Content = "断开";
            SetInputsEnabled(false);
        }
        else if (snapshot.State == ConnectionState.Error)
        {
            ConnectButton.Content = _edgeController.IsRunning ? "断开并重试" : "重新连接";
            SetInputsEnabled(!_edgeController.IsRunning);
        }
        else if (snapshot.State == ConnectionState.Disconnected)
        {
            ConnectButton.Content = ConnectButtonLabel();
        }

        if (_easterEggs.IsJackpot)
        {
            Dispatcher.InvokeAsync(
                () => _easterEggs.ApplyRainbow(this),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void PeerModeText_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not PeerSnapshot peer ||
            peer.ConnectionMode is not (
                PeerConnectionMode.Direct or
                PeerConnectionMode.Ipv6Direct or
                PeerConnectionMode.Relayed or
                PeerConnectionMode.ForcedRelayed or
                PeerConnectionMode.Punching or
                PeerConnectionMode.PunchFailed))
        {
            return;
        }

        e.Handled = true;
        var menu = CreatePeerMenu(element, peer);
        if (peer.ConnectionMode is PeerConnectionMode.PunchFailed or PeerConnectionMode.Relayed)
        {
            var retry = new MenuItem
            {
                Header = "重新尝试 P2P 打洞"
            };
            retry.Click += async (_, _) =>
            {
                retry.IsEnabled = false;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    await _edgeController.RetryPeerPunchAsync(peer.VirtualIp, timeout.Token);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        this,
                        $"无法重新发起双方打洞：{exception.Message}",
                        "P2P 重试失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            };
            menu.Items.Add(retry);
            menu.Items.Add(new Separator());
        }

        var forceRelay = peer.ConnectionMode != PeerConnectionMode.ForcedRelayed;
        var relayItem = new MenuItem
        {
            Header = forceRelay ? "强制使用 pSp 中继" : "取消强制中继，恢复自动 P2P",
            IsCheckable = true,
            IsChecked = !forceRelay
        };
        relayItem.Click += async (_, _) =>
        {
            relayItem.IsEnabled = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _edgeController.SetPeerRelayAsync(peer.VirtualIp, forceRelay, timeout.Token);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    $"无法切换该用户的链路：{exception.Message}",
                    "链路切换失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(relayItem);
        OpenPeerMenu(menu, peer);
    }

    private void PeerIpText_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not PeerSnapshot peer ||
            string.IsNullOrWhiteSpace(peer.VirtualIp))
        {
            return;
        }

        e.Handled = true;
        var menu = CreatePeerMenu(element, peer);
        var copy = new MenuItem { Header = "复制虚拟 IP" };
        copy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(peer.VirtualIp);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    $"无法复制虚拟 IP：{exception.Message}",
                    "复制失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(copy);
        OpenPeerMenu(menu, peer);
    }

    private static ContextMenu CreatePeerMenu(FrameworkElement element, PeerSnapshot peer)
    {
        var menu = new ContextMenu { PlacementTarget = element };
        element.ContextMenu = menu;
        return menu;
    }

    private void OpenPeerMenu(ContextMenu menu, PeerSnapshot peer)
    {
        if (_activePeerMenu is not null)
        {
            _activePeerMenu.IsOpen = false;
        }
        _activePeerMenu = menu;
        _activePeerMenuNodeId = peer.NodeId;
        _activePeerMenuMode = peer.ConnectionMode;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activePeerMenu, menu))
            {
                _activePeerMenu = null;
                _activePeerMenuNodeId = null;
            }
        };
        menu.IsOpen = true;
    }

    private void CloseStalePeerMenu(IReadOnlyList<PeerSnapshot> peers)
    {
        if (_activePeerMenu is null || _activePeerMenuNodeId is null)
        {
            return;
        }
        var current = peers.FirstOrDefault(peer => peer.NodeId == _activePeerMenuNodeId);
        if (current is null || current.ConnectionMode != _activePeerMenuMode)
        {
            _activePeerMenu.IsOpen = false;
        }
    }

    private void SynchronizeDisplayedPeers(IReadOnlyList<PeerSnapshot> peers)
    {
        for (var targetIndex = 0; targetIndex < peers.Count; targetIndex++)
        {
            var desired = peers[targetIndex];
            var currentIndex = -1;
            for (var index = targetIndex; index < _displayPeers.Count; index++)
            {
                if (_displayPeers[index].NodeId == desired.NodeId)
                {
                    currentIndex = index;
                    break;
                }
            }
            if (currentIndex < 0)
            {
                _displayPeers.Insert(targetIndex, desired);
            }
            else
            {
                if (currentIndex != targetIndex)
                {
                    _displayPeers.Move(currentIndex, targetIndex);
                }
                if (_displayPeers[targetIndex] != desired)
                {
                    _displayPeers[targetIndex] = desired;
                }
            }
        }
        while (_displayPeers.Count > peers.Count)
        {
            _displayPeers.RemoveAt(_displayPeers.Count - 1);
        }
    }

    private void UpdateNatIndicator(ConnectionSnapshot snapshot)
    {
        var natType = snapshot.State == ConnectionState.Connected
            ? snapshot.NatType
            : "—";
        NatTypeText.Text = natType;

        var selectedRanks = natType switch
        {
            "NAT1" => new[] { 1 },
            "NAT2" => new[] { 2 },
            "NAT1/2" => new[] { 1, 2 },
            "NAT3" => new[] { 3 },
            "NAT4" => new[] { 4 },
            _ => []
        };
        var selected = selectedRanks.ToHashSet();
        var legends = new[]
        {
            (Rank: 1, Border: Nat1LegendBorder, Overlay: Nat1MutedOverlay),
            (Rank: 2, Border: Nat2LegendBorder, Overlay: Nat2MutedOverlay),
            (Rank: 3, Border: Nat3LegendBorder, Overlay: Nat3MutedOverlay),
            (Rank: 4, Border: Nat4LegendBorder, Overlay: Nat4MutedOverlay)
        };
        foreach (var legend in legends)
        {
            var isCurrent = selected.Contains(legend.Rank);
            legend.Overlay.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;
            legend.Border.BorderThickness = new Thickness(isCurrent ? 2 : 1);
            legend.Border.Opacity = selected.Count == 0 ? 0.62 : 1;
        }

        (NatTypeBadge.Background, NatTypeBadge.BorderBrush, NatTypeText.Foreground, NatHealthDot.Foreground) =
            natType switch
            {
                "NAT1" => (Nat1Soft, Nat1, Nat1, Nat1),
                "NAT2" => (Nat2Soft, Nat2, Nat2, Nat2),
                "NAT1/2" => (Nat12Soft, Nat12, Nat12, Nat12),
                "NAT3" => (Nat3Soft, Nat3, Nat3, Nat3),
                "NAT4" => (Nat4Soft, Nat4, Nat4, Nat4),
                _ => (FindResource("SurfaceAlt") as Brush ?? Gray,
                      FindResource("Border") as Brush ?? Gray,
                      FindResource("TextMuted") as Brush ?? Gray,
                      FindResource("TextMuted") as Brush ?? Gray)
            };

    }

    private void EasterEggs_JackpotActivated(string result) =>
        Dispatcher.InvokeAsync(() => ApplySnapshot(_lastSnapshot));

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Node list tab inside the settings dialog; index follows the XAML tab order.</summary>
    private const int NodesTabIndex = 0;

    private void OpenSettings(int initialTabIndex = 1)
    {
        ShowFromTray();
        var window = new SettingsWindow(_settings, initialTabIndex, _edgeController.IsRunning) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings.Theme = window.SelectedTheme;
        _settings.CloseBehavior = window.SelectedCloseBehavior;
        _settings.LogRetentionDays = window.SelectedLogRetentionDays;
        _settings.ExperimentalIpv6P2p = window.SelectedExperimentalIpv6P2p;
        _settings.Nodes = window.EditedNodes;
        _settings.ActiveNodeId = window.SelectedNodeId;
        _settings.LegacyServer = null;
        _settings.LegacyCommunity = null;
        ((App)Application.Current).ThemeManager.Apply(_settings.Theme);
        ((App)Application.Current).LogCleanup.Configure(_settings.LogRetentionDays);
        RefreshNodeBox();
        SaveSettings();
    }

    private void ShowFromTray()
    {
        // WPF throws from Show()/Visibility while a Closing event is in flight, even
        // when that event cancelled the close. The window is already on screen in that
        // case, so there is nothing to restore.
        if (!IsVisible)
        {
            Show();
        }
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (!_shownTrayTip)
        {
            _shownTrayTip = true;
            _trayIcon.ShowMinimizedTip();
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        NicknameBox.IsEnabled = enabled;
        KeyBox.IsEnabled = enabled;
        RememberKeyBox.IsEnabled = enabled;
        // Switching nodes mid-session would leave the running edge on the old address,
        // so the selector follows the rest of the connection inputs.
        NodeBox.IsEnabled = enabled;
        ManageNodesButton.IsEnabled = enabled;
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        var action = _settings.CloseBehavior;
        if (action == ClosePreference.Ask)
        {
            var dialog = new CloseBehaviorDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            action = dialog.Selection;
            _settings.CloseBehavior = action;
            SaveSettings();
        }

        if (action == ClosePreference.MinimizeToTray)
        {
            HideToTray();
            return;
        }

        // Let WPF finish cancelling this close before the teardown runs; window state
        // changes are not allowed while the Closing event is still on the stack.
        await Dispatcher.Yield(DispatcherPriority.Normal);
        await ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_closeInProgress)
        {
            return;
        }
        _closeInProgress = true;
        // Anything thrown from here would escape an async void handler and take the
        // process down before n3n-edge is stopped, leaving it orphaned on the TAP
        // adapter and blocking the next launch.
        try
        {
            // Deliberately do not restore the window here. Exiting from the tray menu
            // must not pull a minimized window back on screen just to close it.
            IsEnabled = false;
            await _edgeController.DisposeAsync();
            await FinishDiagnosticsAsync();
            _trayIcon.Dispose();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("退出流程", exception, fatal: false);
            EdgeController.KillLaunchedEdgeProcesses();
        }

        _allowClose = true;
        Application.Current.Shutdown();
    }
}
