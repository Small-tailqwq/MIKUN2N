using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace MikuN2N;

public partial class MainWindow : Window
{
    /// <summary>Lines kept in the on-screen log; the full log is on disk.</summary>
    private const int MaxLogLines = 1500;
    private const string DefaultStatusDetail = "填写昵称，连接朋友分享的节点。";
    private const string NoNodeStatusDetail = "先添加朋友分享的节点，再填写昵称开始联机。";

    private readonly SettingsStore _settingsStore = new();
    private readonly EdgeController _edgeController = new();
    private readonly TrayIconService _trayIcon;
    private readonly EasterEggManager _easterEggs;
    private readonly EasterEggVisualController _easterEggVisuals;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly DispatcherTimer _logFlushTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _copiedTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly UpdateService _updates;
    private string? _promptedUpdateVersion;
    private string? _updateTipVersion;
    private AppSettings _settings;
    private ConnectionSnapshot _lastSnapshot = new(
        ConnectionState.Disconnected,
        "尚未连接",
        DefaultStatusDetail);
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _shownTrayTip;
    private bool _connectionBusy;
    private bool _starting;
    private bool _disconnecting;
    private bool _keyRevealed;
    private bool _connectedThisSession;
    private bool _syncingKey;
    private int _logLineCount;
    private DateTimeOffset? _uptimeOrigin;
    private TestDiagnosticsSession? _diagnostics;
    // The node as connected; Settings may edit the active node while the connection runs.
    private SupernodeNode? _connectionNode;
    private readonly ObservableCollection<PeerSnapshot> _displayPeers = [];
    private ContextMenu? _activePeerMenu;
    private string? _activePeerMenuNodeId;
    private PeerConnectionMode _activePeerMenuMode;
    private PeerAssistWindow? _peerAssistWindow;
    private bool _showPeerAssistRequests;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea(this);
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
        if (TestBuildProfile.Current is { } currentProfile &&
            _settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow &&
            !DiagnosticUploadConsent.IsAllowed(_settings, currentProfile))
        {
            DiagnosticUploadConsent.Invalidate(_settings);
            _settingsStore.Save(_settings, KeyBox.Password);
        }
        RefreshDiagnosticsPanel();
        RefreshNodeBox();

        _logFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => FlushLog(), Dispatcher);
        _uptimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshUptime(), Dispatcher);
        _copiedTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            VirtualIpLabel.Text = "虚拟 IP";
        };

        _edgeController.SnapshotChanged += EdgeController_SnapshotChanged;
        _edgeController.LogReceived += EdgeController_LogReceived;
        _edgeController.PeerAssistRequested += EdgeController_PeerAssistRequested;
        Closed += (_, _) => _edgeController.PeerAssistRequested -= EdgeController_PeerAssistRequested;
        _trayIcon = new TrayIconService();
        if (!NotificationRegistration.TryRegister())
            AppendLogLine("Windows 通知身份注册未完成；仍可在主界面查看协助申请。");
        _trayIcon.ShowRequested += (_, _) => Dispatcher.InvokeAsync(ShowFromTray);
        _trayIcon.PeerAssistRequestsRequested += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            _showPeerAssistRequests = true;
            if (!FocusOpenDialog())
            {
                ShowFromTray();
                TryOpenPeerAssistRequests();
            }
        });
        _trayIcon.SettingsRequested += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!FocusOpenDialog())
            {
                OpenSettings();
            }
        });
        _trayIcon.ConnectionRequested += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (FocusOpenDialog())
            {
                return;
            }
            ShowFromTray();
            ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        });
        _trayIcon.ExitRequested += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            // Exiting under an open modal dialog would tear the window down beneath it.
            if (!FocusOpenDialog())
            {
                _ = ExitApplicationAsync();
            }
        });
        _trayIcon.Update(_lastSnapshot, false);
        _updates = ((App)Application.Current).Updates;
        _updates.StateChanged += (_, _) => Dispatcher.InvokeAsync(() => TryPromptUpdate());
        // The first check waits until startup work settles; later ones run once a day.
        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(24);
            if (_settings.AutoCheckUpdates)
            {
                _ = _updates.CheckAndPrepareAsync();
            }
        }, Dispatcher);
        Activated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            TryOpenPeerAssistRequests();
            TryPromptUpdate();
        }, DispatcherPriority.Background);
        Deactivated += (_, _) => NatHelpPopup.IsOpen = false;
        Closing += MainWindow_Closing;
        Loaded += (_, _) => SetInitialFocus();
        InitializeStatusAnimation();
        UpdateTapAvailability();
        UpdateConnectButton();
        UpdateNicknameHint();
    }

    /// <summary>
    /// Keeps a window inside the work area on small or highly scaled screens, where a
    /// fixed height would push the title bar and its close button off screen.
    /// </summary>
    internal static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, area.Width - 32));
        window.Height = Math.Min(window.Height, Math.Max(window.MinHeight, area.Height - 32));
    }

    private void SetInitialFocus()
    {
        if (TapWarningBorder.Visibility == Visibility.Visible)
        {
            InstallTapButton.Focus();
        }
        else if (_settings.Nodes.Count == 0)
        {
            AddFirstNodeButton.Focus();
        }
        else if (NodeBox.SelectedItem is null)
        {
            NodeBox.Focus();
        }
        else if (string.IsNullOrWhiteSpace(NicknameBox.Text))
        {
            NicknameBox.Focus();
        }
        else
        {
            ConnectButton.Focus();
        }
    }

    /// <summary>Brings an already open dialog forward instead of stacking a second one on top.</summary>
    private bool FocusOpenDialog()
    {
        var dialog = OwnedWindows.OfType<Window>().FirstOrDefault(window => window.IsVisible && window is not PeerAssistWindow);
        if (dialog is null)
        {
            return false;
        }
        ShowFromTray();
        dialog.Activate();
        return true;
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
            // Persist only a real switch; the ComboBox also reports its initial
            // selection while loading, which must not rewrite settings on every start.
            var switched = _settings.ActiveNodeId != node.Id;
            if (switched)
                DiagnosticUploadConsent.Invalidate(_settings);
            _settings.ActiveNodeId = node.Id;
            RefreshDiagnosticPreferenceStatus();
            if (switched)
                SaveSettings();
        }
        ClearConnectError();
        RefreshNodeSummary();
    }

    private void ManageNodes_Click(object sender, RoutedEventArgs e) => OpenSettings(NodesTabIndex);

    private void AddFirstNode_Click(object sender, RoutedEventArgs e) => AddFirstNode();

    /// <summary>
    /// First-run path: add a node straight from the main window instead of sending a
    /// new player through the settings dialog and its separate save step.
    /// </summary>
    private bool AddFirstNode()
    {
        var dialog = new NodeEditDialog(null, _settings.Nodes.Select(node => node.Name)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }
        _settings.Nodes.Add(dialog.Result);
        if (_settings.ActiveNodeId != dialog.Result.Id)
        {
            DiagnosticUploadConsent.Invalidate(_settings);
        }
        _settings.ActiveNodeId = dialog.Result.Id;
        RefreshNodeBox();
        SaveSettings();
        RefreshDiagnosticPreferenceStatus();
        ClearConnectError();
        if (_lastSnapshot.State is ConnectionState.Disconnected && StatusDetail.Text == NoNodeStatusDetail)
        {
            StatusDetail.Text = DefaultStatusDetail;
        }
        SetInitialFocus();
        return true;
    }

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
        var hasNodes = _settings.Nodes.Count > 0;
        NodeAddressText.Text = node is null ? "请选择一个节点" : $"地址：{node.Server}";
        NodeAddressText.ToolTip = node?.Server;
        NodeCommunityText.Text = node is null ? "小组：—" : $"小组：{node.Community}";
        NodeCommunityText.ToolTip = node?.Community;
        RoomNameText.Text = $"小组：{(string.IsNullOrWhiteSpace(node?.Community) ? "—" : node.Community)}";
        NoNodeBorder.Visibility = hasNodes ? Visibility.Collapsed : Visibility.Visible;
        NodeBox.Visibility = hasNodes ? Visibility.Visible : Visibility.Collapsed;
        ManageNodesButton.Visibility = hasNodes ? Visibility.Visible : Visibility.Collapsed;
        NodeDetailsPanel.Visibility = hasNodes ? Visibility.Visible : Visibility.Collapsed;
        if (!hasNodes && _lastSnapshot.State is ConnectionState.Disconnected && !_edgeController.IsRunning)
        {
            StatusDetail.Text = NoNodeStatusDetail;
        }
        UpdateConnectButton();
    }

    private string ConnectButtonLabel()
    {
        var community = (NodeBox?.SelectedItem as SupernodeNode)?.Community;
        return string.IsNullOrWhiteSpace(community) ? "连接" : $"连接到 {community}";
    }

    /// <summary>
    /// The only place that sets the connect button, so its text always matches what a
    /// click will do. While a session runs the button drops the prominent style: a
    /// stray click there disconnects every friend.
    /// </summary>
    private void UpdateConnectButton()
    {
        var running = _edgeController.IsRunning || _starting;
        ConnectButton.Content = _disconnecting
            ? "正在断开…"
            : !running
                ? ConnectButtonLabel()
                : _lastSnapshot.State is ConnectionState.Connected ||
                  (_lastSnapshot.State is ConnectionState.Reconnecting && _connectedThisSession)
                    ? "断开连接"
                    : "取消连接";
        if (running || _disconnecting)
        {
            ConnectButton.ClearValue(StyleProperty);
        }
        else
        {
            ConnectButton.SetResourceReference(StyleProperty, "PrimaryButton");
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy) return;
        if (_edgeController.IsRunning)
        {
            _connectionBusy = true;
            try
            {
                _disconnecting = true;
                UpdateConnectButton();
                ConnectButton.IsEnabled = false;
                await _edgeController.StopAsync();
                await FinishDiagnosticsAsync();
                SetInputsEnabled(true);
            }
            finally
            {
                _disconnecting = false;
                _connectionBusy = false;
                ConnectButton.IsEnabled = true;
                UpdateConnectButton();
                RefreshNodeSummary();
            }
            return;
        }

        if (_settings.ActiveNode is null && NodeBox.SelectedItem is not SupernodeNode)
        {
            if (_settings.Nodes.Count == 0)
            {
                if (!AddFirstNode())
                {
                    return;
                }
            }
            else
            {
                ShowConnectError("请先在上方选择要连接的节点。", NodeBox);
                return;
            }
        }

        if (!TryValidate(out var error, out var focusTarget))
        {
            ShowConnectError(error, focusTarget);
            return;
        }
        ClearConnectError();

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
                AppendLogLine($"已结束 {conflictingProcesses.Count} 个确认的旧 n2n/n3n 进程。");
            }
            catch (Exception exception)
            {
                ThemedMessageDialog.Show(this, exception.Message, "无法清理旧进程", MessageKind.Error);
                return;
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        SaveSettings();
        _pendingLog.Enqueue($"{Environment.NewLine}===== 开始新的连接 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        var node = _settings.ActiveNode ?? NodeBox.SelectedItem as SupernodeNode;
        if (node is null)
        {
            return;
        }

        SetInputsEnabled(false);
        _starting = true;
        _connectedThisSession = false;
        UpdateConnectButton();

        try
        {
            _connectionBusy = true;
            await FinishDiagnosticsAsync();
            _connectionNode = CopyNode(node);
            var (receiver, certificateChanged) = TestBuildProfile.Current is { } profile
                ? (profile, false)
                : _settings.ExperimentalDiagnosticUpload
                    ? await ResolveNodeReceiverAsync(node)
                    : ((TestBuildProfile?)null, false);
            if (_closeInProgress) return;
            if (receiver is not null)
            {
                var consent = ResolveDiagnosticConsent(receiver, certificateChanged);
                if (_closeInProgress) return;
                _diagnostics = CreateDiagnostics(receiver, consent, "连接前");
                RefreshDiagnosticButtons();
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
            ApplySnapshot(new ConnectionSnapshot(
                ConnectionState.Error,
                "无法开始连接",
                exception.Message));
        }
        finally
        {
            _starting = false;
            _connectionBusy = false;
            UpdateConnectButton();
        }
    }

    private void ShowConnectError(string message, Control? focusTarget)
    {
        ConnectErrorText.Text = message;
        ConnectErrorText.Visibility = Visibility.Visible;
        if (focusTarget is not null)
        {
            focusTarget.BringIntoView();
            focusTarget.Focus();
        }
    }

    private void ClearConnectError()
    {
        if (ConnectErrorText is not null)
        {
            ConnectErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void ConnectInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter only starts a connection. The button is deliberately not IsDefault, or
        // Enter pressed later in a text box would disconnect everyone.
        if (e.Key == Key.Enter && !_edgeController.IsRunning && !_connectionBusy)
        {
            e.Handled = true;
            ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        }
    }

    private void NicknameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearConnectError();
        UpdateNicknameHint();
    }

    private void UpdateNicknameHint()
    {
        if (NicknameHintText is null)
        {
            return;
        }
        var used = Encoding.UTF8.GetByteCount(NicknameBox.Text.Trim());
        var left = NodeAddress.MaxNicknameBytes - used;
        if (left < 0)
        {
            NicknameHintText.Text = $"昵称太长：超出 {-left} 个字节（一个汉字约占 3 个），请缩短。";
            NicknameHintText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        else
        {
            NicknameHintText.Text = left <= 6
                ? $"朋友会通过这个名字辨认你。还可输入约 {left} 个英文字符。"
                : "朋友会通过这个名字辨认你。";
            NicknameHintText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ClearConnectError();
        if (_syncingKey || !_keyRevealed)
        {
            return;
        }
        _syncingKey = true;
        KeyPlainBox.Text = KeyBox.Password;
        _syncingKey = false;
    }

    private void KeyPlainBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingKey)
        {
            return;
        }
        _syncingKey = true;
        KeyBox.Password = KeyPlainBox.Text;
        _syncingKey = false;
        ClearConnectError();
    }

    private void KeyRevealButton_Click(object sender, RoutedEventArgs e)
    {
        _keyRevealed = !_keyRevealed;
        _syncingKey = true;
        KeyPlainBox.Text = _keyRevealed ? KeyBox.Password : string.Empty;
        _syncingKey = false;
        KeyPlainBox.Visibility = _keyRevealed ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Visibility = _keyRevealed ? Visibility.Collapsed : Visibility.Visible;
        KeyRevealButton.Content = _keyRevealed ? "隐藏" : "显示";
        if (_keyRevealed)
        {
            KeyPlainBox.Focus();
            KeyPlainBox.CaretIndex = KeyPlainBox.Text.Length;
        }
        else
        {
            KeyBox.Focus();
        }
    }

    private async void StopUpload_Click(object sender, RoutedEventArgs e)
    {
        StopUploadButton.IsEnabled = false;
        if (_diagnostics is { } diagnostics)
            await diagnostics.StopUploadAsync();
    }

    private static SupernodeNode CopyNode(SupernodeNode node) =>
        new() { Id = node.Id, Name = node.Name, Server = node.Server, Community = node.Community };

    private static bool SameNode(SupernodeNode? a, SupernodeNode? b) =>
        a is not null && b is not null && a.Id == b.Id && a.Name == b.Name &&
        a.Server == b.Server && a.Community == b.Community;

    /// <summary>Creates the recording for a connection; null when it cannot be written.</summary>
    private TestDiagnosticsSession? CreateDiagnostics(TestBuildProfile receiver, bool consent, string checkedWhen)
    {
        try
        {
            TestDiagnosticsSession? diagnostics = null;
            // A replaced recording keeps reporting while it closes; only the current one
            // (or the last one, after disconnecting) may own the panel.
            diagnostics = new TestDiagnosticsSession(receiver, consent, KeyBox.Password,
                status => Dispatcher.InvokeAsync(() =>
                {
                    if (_diagnostics is null || ReferenceEquals(_diagnostics, diagnostics))
                        UpdateDiagnosticStatus(status);
                }));
            if (receiver.IsNodeReceiver && receiver.CertificateSha256.Length == 0)
                ReportUnreachableReceiver(receiver, checkedWhen);
            return diagnostics;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsStatusText.Text = "详细日志无法写入，本次不会上传日志。";
            return null;
        }
    }

    private void ReportUnreachableReceiver(TestBuildProfile receiver, string checkedWhen)
    {
        DiagnosticsStatusText.Text = "节点服务器没有开启日志接收，详细日志只保存在本机。";
        // An enabled preference with no receiver answer used to skip consent in
        // silence: no prompt, no upload, and one panel line as the only sign.
        // The player asked to upload, so name the unreachable receiver instead.
        if (_settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysDeny) return;
        ThemedMessageDialog.Show(this,
            $"{checkedWhen}检查了 {new Uri(receiver.UploadUrl).Authority} 的日志接收端，没有得到响应。" +
            "本次详细日志只保存在本机，不会上传。\n\n" +
            $"若这台服务器应当接收日志，请确认它的诊断接收端正在运行，并且已放行 TCP {TestBuildProfile.NodeReceiverPort}（主机防火墙和云安全组都要放行）。" +
            "之后可在主窗口点击“恢复日志”重新检查，不必断开联机。",
            "本次不上传诊断日志", MessageKind.Warning);
    }

    /// <summary>
    /// Applies a settings save to the running connection, through the same receiver check
    /// and consent as connecting. Only records made after the decision are uploaded.
    /// </summary>
    private async Task ApplyDiagnosticSettingsAsync(DiagnosticUploadPreference previousUpload, string previousTarget,
        bool previousNodeUpload)
    {
        var connectionId = _edgeController.ConnectionId;
        if (connectionId == 0 || _starting) return;
        var diagnostics = _diagnostics;
        var change = DiagnosticUploadConsent.PlanSettingsChange(_settings, previousUpload, previousTarget,
            previousNodeUpload, TestBuildProfile.Current is not null, diagnostics is not null,
            diagnostics?.UploadAllowed == true, diagnostics?.Receiver.IsNodeReceiver == true);
        if (change == ActiveDiagnosticsChange.StopUpload)
        {
            await diagnostics!.StopUploadAsync();
            return;
        }
        if (change == ActiveDiagnosticsChange.None) return;
        // Consent binds to the node; an edit to the connected node waits for the next connection.
        if (TestBuildProfile.Current is null && !SameNode(_connectionNode, _settings.ActiveNode))
        {
            DiagnosticsStatusText.Text = "当前节点信息已修改，日志上传设置将在重新连接后生效。";
            return;
        }
        await AuthorizeActiveDiagnosticsAsync(connectionId, diagnostics, "保存设置后");
    }

    /// <summary>
    /// Runs the receiver check and consent for the running connection, re-checking a node
    /// receiver that did not answer before, and attaches a new recording when one is needed.
    /// </summary>
    private async Task AuthorizeActiveDiagnosticsAsync(int connectionId, TestDiagnosticsSession? diagnostics,
        string checkedWhen)
    {
        var receiver = diagnostics?.Receiver ?? TestBuildProfile.Current;
        var certificateChanged = false;
        if (receiver is null || (receiver.IsNodeReceiver && receiver.CertificateSha256.Length == 0))
        {
            if (_connectionNode is not { } node) return;
            (receiver, certificateChanged) = await ResolveNodeReceiverAsync(node);
            if (_closeInProgress || _edgeController.ConnectionId != connectionId ||
                !ReferenceEquals(_diagnostics, diagnostics)) return;
        }
        var consent = ResolveDiagnosticConsent(receiver, certificateChanged);
        if (_closeInProgress || _edgeController.ConnectionId != connectionId ||
            !ReferenceEquals(_diagnostics, diagnostics)) return;
        if (diagnostics is not null && ReferenceEquals(receiver, diagnostics.Receiver))
        {
            if (consent && !diagnostics.UploadAllowed)
                await diagnostics.ResumeAsync(true);
            RefreshDiagnosticButtons();
            return;
        }
        if (diagnostics is not null && receiver.CertificateSha256.Length == 0)
        {
            // Still no receiver: keep the existing local recording rather than starting another.
            ReportUnreachableReceiver(receiver, checkedWhen);
            return;
        }
        if (diagnostics is not null && !consent)
        {
            // Replaces the "checking receiver" line left by the probe above.
            DiagnosticsStatusText.Text = "未同意上传，详细日志只保存在本机。";
            return;
        }
        if (CreateDiagnostics(receiver, consent, checkedWhen) is not { } replacement) return;
        if (!_edgeController.AttachDiagnostics(connectionId, replacement, diagnostics))
        {
            await replacement.DisposeAsync();
            return;
        }
        _diagnostics = replacement;
        RefreshDiagnosticButtons();
        if (diagnostics is not null)
            await diagnostics.DisposeAsync();
    }

    private async Task<(TestBuildProfile Receiver, bool CertificateChanged)> ResolveNodeReceiverAsync(SupernodeNode node)
    {
        var host = NodeServiceProbe.PrimaryHost(node.Server) ?? string.Empty;
        DiagnosticsStatusText.Text = "正在检查节点服务器是否开启日志接收…";
        var fingerprint = host.Length == 0 ? null
            : await NodeServiceProbe.FetchReceiverCertificateAsync(host, CancellationToken.None);
        var pinned = _settings.DiagnosticCertificatePins.GetValueOrDefault(node.Id);
        return (TestBuildProfile.ForNode(node, host, fingerprint ?? string.Empty),
            fingerprint is not null && pinned is not null &&
            !string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    private bool ResolveDiagnosticConsent(TestBuildProfile profile, bool certificateChanged = false)
    {
        // A node that answered with no receiver has nothing to consent to.
        if (profile.IsNodeReceiver && profile.CertificateSha256.Length == 0) return false;
        if (_settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysDeny) return false;
        if (DiagnosticUploadConsent.IsAllowed(_settings, profile)) return true;
        bool consent;
        if (_settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow && profile.IsNodeReceiver &&
            _settings.DiagnosticUploadTarget.Length == 0 && !certificateChanged)
        {
            // "Always allow" chosen in Settings before this node's receiver certificate was
            // known: the first connection binds it after the persistent confirmation.
            consent = new LogUploadConsentDialog(profile, persistent: true, node: _settings.ActiveNode) { Owner = this }
                .ShowDialog() == true;
            if (consent)
                _settings.DiagnosticUploadTarget = DiagnosticUploadConsent.Target(profile, _settings.ActiveNode);
            else
                DiagnosticUploadConsent.Invalidate(_settings);
        }
        else
        {
            if (_settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow)
                DiagnosticUploadConsent.Invalidate(_settings);
            consent = new LogUploadConsentDialog(profile, node: _settings.ActiveNode, certificateChanged: certificateChanged)
                { Owner = this }.ShowDialog() == true;
        }
        if (consent && profile.IsNodeReceiver)
            _settings.DiagnosticCertificatePins[profile.Node.Id] = profile.CertificateSha256;
        SaveSettings();
        return consent;
    }

    private void RefreshDiagnosticPreferenceStatus()
    {
        if (_diagnostics is not null) return;
        if (TestBuildProfile.Current is { } profile)
            DiagnosticsStatusText.Text = _settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysDeny
                ? "已设为始终拒绝上传，日志仅保存在本机"
                : DiagnosticUploadConsent.IsAllowed(_settings, profile)
                    ? "当前节点已授权自动上传，可随时停止"
                    : "连接前会询问日志上传授权；更改节点后须重新授权";
        else if (_settings.ExperimentalDiagnosticUpload)
            DiagnosticsStatusText.Text = _settings.DiagnosticUpload switch
            {
                DiagnosticUploadPreference.AlwaysDeny => "已设为始终拒绝上传，日志仅保存在本机",
                // The receiver certificate is only seen when connecting, so the first one
                // still asks once; saying so keeps that prompt from looking like a fault.
                DiagnosticUploadPreference.AlwaysAllow when _settings.DiagnosticUploadTarget.Length == 0 =>
                    "已选始终允许：下次连接时核对节点服务器证书并确认一次，之后自动上传",
                DiagnosticUploadPreference.AlwaysAllow => "当前节点已授权自动上传，连接时核对服务器证书，可随时停止",
                _ => "连接时检查节点服务器是否开启日志接收，上传前会征得你的同意"
            };
    }

    private void RefreshDiagnosticsPanel()
    {
        DiagnosticsPanel.Visibility = TestBuildProfile.Current is not null || _settings.ExperimentalDiagnosticUpload
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshDiagnosticPreferenceStatus();
    }

    private void UpdateDiagnosticStatus(string status)
    {
        DiagnosticsStatusText.Text = status;
        RefreshDiagnosticButtons();
    }

    private void RefreshDiagnosticButtons()
    {
        StopUploadButton.IsEnabled = _diagnostics?.UploadAllowed == true;
        ResumeDiagnosticsButton.IsEnabled = _diagnostics is not null && !_closeInProgress;
    }

    private async void ResumeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is not { } diagnostics) return;
        ResumeDiagnosticsButton.IsEnabled = false;
        try
        {
            var receiver = diagnostics.Receiver;
            var connectionId = _edgeController.ConnectionId;
            if (receiver.IsNodeReceiver && !SameNode(_connectionNode, _settings.ActiveNode))
            {
                await diagnostics.ResumeAsync(false);
                DiagnosticsStatusText.Text = "当前节点信息已修改，日志只在本机恢复记录；重新连接后才能上传。";
            }
            else if (receiver.IsNodeReceiver && receiver.CertificateSha256.Length == 0 && connectionId != 0 &&
                     _settings.DiagnosticUpload != DiagnosticUploadPreference.AlwaysDeny)
            {
                // The receiver did not answer when connecting. Resuming that recording could
                // never upload, so check the receiver again and switch recordings if it answers.
                await AuthorizeActiveDiagnosticsAsync(connectionId, diagnostics, "恢复日志时");
                if (ReferenceEquals(_diagnostics, diagnostics) && !_closeInProgress)
                    await diagnostics.ResumeAsync(false);
            }
            else
            {
                var consent = ResolveDiagnosticConsent(receiver);
                if (_closeInProgress) return;
                await diagnostics.ResumeAsync(consent);
            }
        }
        catch (Exception exception)
        {
            CrashLogService.Record("恢复诊断日志", exception, fatal: false);
            ThemedMessageDialog.Show(this, $"暂时无法恢复日志：{exception.Message}", "恢复日志失败", MessageKind.Warning);
        }
        RefreshDiagnosticButtons();
    }

    private async Task FinishDiagnosticsAsync()
    {
        var diagnostics = _diagnostics;
        _diagnostics = null;
        StopUploadButton.IsEnabled = false;
        ResumeDiagnosticsButton.IsEnabled = false;
        if (diagnostics is not null)
            await diagnostics.DisposeAsync();
    }

    private bool TryValidate(out string error, out Control? focusTarget)
    {
        focusTarget = null;
        if (!EdgeController.HasTapAdapter())
        {
            TapWarningBorder.Visibility = Visibility.Visible;
            focusTarget = InstallTapButton;
            error = "还没有安装虚拟网卡。请先点击上方的“安装网络组件”，安装完成后再连接。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(NicknameBox.Text))
        {
            focusTarget = NicknameBox;
            error = "请填写一个昵称，朋友们会用它辨认你。";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(NicknameBox.Text.Trim()) > NodeAddress.MaxNicknameBytes)
        {
            focusTarget = NicknameBox;
            error = "昵称太长，请缩短到 31 个英文字符或大约 10 个汉字以内。";
            return false;
        }
        var node = _settings.ActiveNode ?? NodeBox.SelectedItem as SupernodeNode;
        if (node is null)
        {
            focusTarget = NodeBox;
            error = "请先在下方“当前节点”里选择一个节点。";
            return false;
        }
        if (!NodeAddress.TryNormalize(node.Server, out _, out var serverError))
        {
            focusTarget = ManageNodesButton;
            error = $"当前节点的服务器地址不正确：{serverError}请点击“管理节点”修改。";
            return false;
        }
        if (!NodeAddress.TryValidateCommunity(node.Community, out var communityError))
        {
            focusTarget = ManageNodesButton;
            error = $"当前节点的小组名称不正确：{communityError}请点击“管理节点”修改。";
            return false;
        }
        if (KeyBox.Password.Any(character => character > 127))
        {
            focusTarget = _keyRevealed ? KeyPlainBox : KeyBox;
            error = "联机密钥只能使用英文字母、数字和常见英文符号。";
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
            ThemedMessageDialog.Show(this, "程序包中缺少虚拟网卡安装器，请重新下载完整安装包。", "程序包不完整",
                MessageKind.Error);
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
            ThemedMessageDialog.Show(this, $"无法启动安装程序：{exception.Message}", "安装失败", MessageKind.Error);
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
            ExperimentalIpv6P2p = _settings.ExperimentalIpv6P2p,
            ExperimentalDiagnosticUpload = _settings.ExperimentalDiagnosticUpload,
            DiagnosticUpload = _settings.DiagnosticUpload,
            DiagnosticUploadTarget = _settings.DiagnosticUploadTarget,
            DiagnosticCertificatePins = _settings.DiagnosticCertificatePins,
            AutoCheckUpdates = _settings.AutoCheckUpdates,
            SkippedUpdateVersion = _settings.SkippedUpdateVersion
        };
        _settingsStore.Save(_settings, KeyBox.Password);
    }

    private void EdgeController_SnapshotChanged(object? sender, ConnectionSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
    }

    private void EdgeController_LogReceived(object? sender, string line) => AppendLogLine(line);

    /// <summary>Thread-safe; lines are written in batches so a busy session cannot stall the UI.</summary>
    private void AppendLogLine(string line) => _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty)
        {
            return;
        }
        var text = new StringBuilder();
        var added = 0;
        while (_pendingLog.TryDequeue(out var line))
        {
            text.Append(line).Append(Environment.NewLine);
            added += 1 + line.Count(character => character == '\n');
        }
        // Only follow the tail when the reader is already there; scrolling back up to
        // read something must not be undone by the next line.
        var atBottom = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        LogBox.AppendText(text.ToString());
        _logLineCount += added;
        if (_logLineCount > MaxLogLines + 300)
        {
            var content = LogBox.Text;
            var cut = 0;
            for (var drop = _logLineCount - MaxLogLines; drop > 0 && cut >= 0; drop--)
            {
                cut = content.IndexOf('\n', cut) is var next and >= 0 ? next + 1 : -1;
            }
            if (cut > 0)
            {
                LogBox.Text = content[cut..];
                _logLineCount = MaxLogLines;
            }
            atBottom = true;
        }
        if (atBottom)
        {
            LogBox.ScrollToEnd();
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MikuN2N", "logs");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ThemedMessageDialog.Show(this, $"无法打开日志目录：{exception.Message}", "打开失败", MessageKind.Warning);
        }
    }

    private void VirtualIpText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IPAddress.TryParse(VirtualIpText.Text, out _))
        {
            return;
        }
        VirtualIpLabel.Text = ClipboardService.TrySetText(VirtualIpText.Text)
            ? "虚拟 IP · 已复制"
            : "虚拟 IP · 复制失败，请重试";
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void ApplySnapshot(ConnectionSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _connectedThisSession |= snapshot.State == ConnectionState.Connected;
        _trayIcon.Update(snapshot, _edgeController.IsRunning);
        StatusTitle.Text = snapshot.State == ConnectionState.Connected ? "已连接" : snapshot.Summary;
        StatusDetail.Text = snapshot.State == ConnectionState.Disconnected && _settings.Nodes.Count == 0
            ? NoNodeStatusDetail
            : snapshot.Detail;
        VirtualIpText.Text = snapshot.VirtualIp;
        var ipCopyable = IPAddress.TryParse(snapshot.VirtualIp, out _);
        VirtualIpText.Cursor = ipCopyable ? Cursors.Hand : null;
        VirtualIpText.ToolTip = ipCopyable ? "点击复制，发给朋友用于游戏内直连" : null;
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
        SupernodeText.Text = $"节点：{snapshot.SupernodeText}";
        _uptimeOrigin = snapshot.Uptime is { } uptime ? DateTimeOffset.Now - uptime : null;
        RefreshUptime();
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
        RefreshPeerAssistRequests();
        PeersEmptyText.Visibility = displayPeers.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        PeersEmptyDetail.Text = snapshot.State switch
        {
            ConnectionState.Connecting or ConnectionState.Reconnecting => "正在连接，稍后显示朋友…",
            ConnectionState.Connected =>
                "还没有看到朋友。请确认双方选择了同一个节点、小组名称和联机密钥，并且对方也已连接。",
            _ => "连接后，在线朋友会自动出现在这里。"
        };
        DiscoveryBadge.Visibility = snapshot.State == ConnectionState.Connected
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateStatusIndicator(snapshot.State);

        if (snapshot.State == ConnectionState.Reconnecting)
        {
            SetInputsEnabled(false);
        }
        else if (snapshot.State == ConnectionState.Error)
        {
            SetInputsEnabled(!_edgeController.IsRunning);
        }
        UpdateConnectButton();
        TryPromptUpdate();

        if (_easterEggs.IsJackpot)
        {
            Dispatcher.InvokeAsync(
                () => _easterEggs.ApplyRainbow(this),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Ticks locally between the 1.5 s snapshots so the clock does not skip seconds.</summary>
    private void RefreshUptime()
    {
        if (_uptimeOrigin is not { } origin || _lastSnapshot.State != ConnectionState.Connected)
        {
            UptimeText.Text = "—";
            return;
        }
        var uptime = DateTimeOffset.Now - origin;
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }
        UptimeText.Text = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private void PeersGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(PeersGrid, source) is not DataGridRow { Item: PeerSnapshot peer } row)
        {
            return;
        }
        // Handled either way, so WPF never falls back to a menu left on a recycled cell.
        e.Handled = true;
        row.IsSelected = true;
        OpenPeerMenu(peer, row, PlacementMode.MousePoint);
    }

    private void PeersGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shiftF10 = Keyboard.Modifiers == ModifierKeys.Shift &&
                       (e.Key == Key.F10 || (e.Key == Key.System && e.SystemKey == Key.F10));
        if ((e.Key != Key.Apps && !shiftF10) || PeersGrid.SelectedItem is not PeerSnapshot peer)
        {
            return;
        }
        e.Handled = true;
        var row = PeersGrid.ItemContainerGenerator.ContainerFromItem(peer) as DataGridRow;
        OpenPeerMenu(peer, row is null ? PeersGrid : (UIElement)row,
            row is null ? PlacementMode.Center : PlacementMode.Bottom);
    }

    private void OpenPeerMenu(PeerSnapshot peer, UIElement target, PlacementMode placement)
    {
        if (string.IsNullOrWhiteSpace(peer.VirtualIp))
        {
            return;
        }
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };

        var copy = new MenuItem { Header = "复制虚拟 IP" };
        copy.Click += (_, _) =>
        {
            if (!ClipboardService.TrySetText(peer.VirtualIp))
            {
                ThemedMessageDialog.Show(this, "剪贴板正被其他程序占用，请稍后再试。", "复制失败", MessageKind.Warning);
            }
        };
        menu.Items.Add(copy);

        var assist = new MenuItem { Header = "选择协助转发…" };
        assist.Click += (_, _) => OpenPeerAssist(peer);
        menu.Items.Add(assist);

        if (peer.ConnectionMode is PeerConnectionMode.PunchFailed or PeerConnectionMode.Relayed)
        {
            var retry = new MenuItem { Header = "重新尝试直连" };
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
                    ThemedMessageDialog.Show(this, $"无法重新发起双方直连尝试：{exception.Message}", "重试失败",
                        MessageKind.Warning);
                }
            };
            menu.Items.Add(new Separator());
            menu.Items.Add(retry);
        }

        if (peer.ConnectionMode is PeerConnectionMode.Direct or PeerConnectionMode.Ipv6Direct or
            PeerConnectionMode.Relayed or PeerConnectionMode.ForcedRelayed or
            PeerConnectionMode.Punching or PeerConnectionMode.PunchFailed or PeerConnectionMode.PeerAssisted)
        {
            var forced = peer.ConnectionMode == PeerConnectionMode.ForcedRelayed;
            var relayItem = new MenuItem
            {
                Header = "始终经服务器中转",
                IsCheckable = true,
                IsChecked = forced,
                ToolTip = forced ? "再次点击恢复自动直连" : "直连不稳定时可手动改走服务器，双方会同步切换"
            };
            relayItem.Click += async (_, _) =>
            {
                relayItem.IsEnabled = false;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _edgeController.SetPeerRelayAsync(peer.VirtualIp, !forced, timeout.Token);
                }
                catch (Exception exception)
                {
                    ThemedMessageDialog.Show(this, $"无法切换这位朋友的线路：{exception.Message}", "线路切换失败",
                        MessageKind.Warning);
                }
            };
            if (menu.Items.Count == 2)
            {
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(relayItem);
        }

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

    private void EdgeController_PeerAssistRequested(object? sender, PeerAssistRequest request)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshPeerAssistRequests();
            AppendLogLine($"{request.RequesterNickname} 请求协助与 {request.TargetNickname} 通信，可在「协助申请」中同意或拒绝。");
            var pending = _edgeController.GetPeerAssistRequests();
            if (pending.Any(item => item.CircuitId == request.CircuitId))
            {
                _trayIcon.ShowPeerAssistRequestTip(request, pending.Count);
            }
        });
    }

    private void RefreshPeerAssistRequests()
    {
        var pending = _edgeController.GetPeerAssistRequests();
        _trayIcon.UpdatePeerAssistRequests(pending);
        var count = pending.Count;
        PeerAssistRequestsButton.Content = $"协助申请（{count}）";
        PeerAssistRequestsButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PeerAssistRequests_Click(object sender, RoutedEventArgs e) => OpenPeerAssist(null);

    private void TryOpenPeerAssistRequests()
    {
        if (!_showPeerAssistRequests ||
            OwnedWindows.OfType<Window>().Any(window => window.IsVisible && window is not PeerAssistWindow))
        {
            return;
        }
        _showPeerAssistRequests = false;
        RefreshPeerAssistRequests();
        if (_edgeController.GetPeerAssistRequests().Count > 0)
        {
            OpenPeerAssist(null);
        }
    }

    private void OpenPeerAssist(PeerSnapshot? target)
    {
        if (_peerAssistWindow is { } existing)
        {
            existing.SetTarget(target);
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.Activate();
            return;
        }
        var window = new PeerAssistWindow(_edgeController, target) { Owner = this };
        _peerAssistWindow = window;
        window.RequestsChanged += (_, _) => RefreshPeerAssistRequests();
        window.Closed += (_, _) =>
        {
            _peerAssistWindow = null;
            RefreshPeerAssistRequests();
        };
        window.Show();
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
        NatTypeText.Text = natType == "—" ? "待检测" : natType;
        NatConditionText.Text = natType switch
        {
            "NAT1" or "NAT2" or "NAT1/2" => "直连条件较好",
            "NAT3" => "直连需要双方配合",
            "NAT4" => "直连条件受限",
            "检测中" => "正在检测网络类型",
            "检测不可用" or "未知" => "暂无检测结果",
            _ => "连接后自动检测"
        };
        NatExplainText.Text = natType switch
        {
            "NAT1" or "NAT2" or "NAT1/2" => "网络限制较少，较容易建立直连。",
            "NAT3" => "传入连接受限，程序会尝试打洞直连。",
            "NAT4" => "端口映射随连接对象变化，直连较困难。",
            "检测中" => "正在检测网络类型…",
            "检测不可用" or "未知" => "暂未获取到 NAT 类型。",
            _ => "连接后自动检测 NAT 类型。"
        };
        var hint = snapshot.State == ConnectionState.Connected ? snapshot.NetworkHint : null;
        NetworkHintText.Text = hint ?? string.Empty;
        NetworkHintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;

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
            (Rank: 1, Border: Nat1LegendBorder, Text: Nat1LegendText),
            (Rank: 2, Border: Nat2LegendBorder, Text: Nat2LegendText),
            (Rank: 3, Border: Nat3LegendBorder, Text: Nat3LegendText),
            (Rank: 4, Border: Nat4LegendBorder, Text: Nat4LegendText)
        };
        foreach (var legend in legends)
        {
            var isCurrent = selected.Contains(legend.Rank);
            legend.Border.SetResourceReference(Border.BackgroundProperty, isCurrent ? "AccentSoft" : "SurfaceAlt");
            legend.Border.SetResourceReference(Border.BorderBrushProperty, isCurrent ? "Accent" : "Border");
            legend.Text.SetResourceReference(TextBlock.ForegroundProperty, isCurrent ? "AccentText" : "TextSecondary");
        }

        var tone = natType switch
        {
            "NAT1" or "NAT2" or "NAT1/2" => "Success",
            "NAT3" or "NAT4" => "Warning",
            _ => "TextMuted"
        };
        NatHealthDot.SetResourceReference(TextBlock.ForegroundProperty, tone);
    }

    private void NatHelp_Click(object sender, RoutedEventArgs e)
    {
        NatHelpPopup.StaysOpen = false;
        NatHelpPopup.IsOpen = true;
    }

    private void NatHelp_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!NatHelpPopup.IsOpen)
        {
            NatHelpPopup.StaysOpen = true;
            NatHelpPopup.IsOpen = true;
        }
    }

    private void NatHelp_MouseLeave(object sender, MouseEventArgs e)
    {
        if (NatHelpPopup.StaysOpen)
        {
            NatHelpPopup.IsOpen = false;
        }
    }

    private void NatHelp_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NatHelpPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void NatHelp_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        NatHelpPopup.IsOpen = false;

    private void EasterEggs_JackpotActivated(string result) =>
        Dispatcher.InvokeAsync(() => ApplySnapshot(_lastSnapshot));

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Node list tab inside the settings dialog; index follows the XAML tab order.</summary>
    private const int NodesTabIndex = 0;

    private async void OpenSettings(int initialTabIndex = 1)
    {
        ShowFromTray();
        var window = new SettingsWindow(_settings, initialTabIndex, _edgeController.IsRunning) { Owner = this };
        if (window.ShowDialog() != true)
        {
            TryPromptUpdate();
            return;
        }

        _settings.Theme = window.SelectedTheme;
        _settings.CloseBehavior = window.SelectedCloseBehavior;
        _settings.LogRetentionDays = window.SelectedLogRetentionDays;
        _settings.ExperimentalIpv6P2p = window.SelectedExperimentalIpv6P2p;
        var previousNodeUpload = _settings.ExperimentalDiagnosticUpload;
        _settings.ExperimentalDiagnosticUpload = window.SelectedExperimentalDiagnosticUpload;
        _settings.AutoCheckUpdates = window.SelectedAutoCheckUpdates;
        var previousUpload = _settings.DiagnosticUpload;
        var previousTarget = _settings.DiagnosticUploadTarget;
        _settings.DiagnosticUpload = window.SelectedDiagnosticUpload;
        _settings.DiagnosticUploadTarget = window.SelectedDiagnosticUploadTarget;
        _settings.Nodes = window.EditedNodes;
        _settings.ActiveNodeId = window.SelectedNodeId;
        _settings.LegacyServer = null;
        _settings.LegacyCommunity = null;
        ((App)Application.Current).ThemeManager.Apply(_settings.Theme);
        ((App)Application.Current).LogCleanup.Configure(_settings.LogRetentionDays);
        RefreshNodeBox();
        SaveSettings();
        RefreshDiagnosticsPanel();
        try
        {
            await ApplyDiagnosticSettingsAsync(previousUpload, previousTarget, previousNodeUpload);
        }
        catch (Exception exception)
        {
            CrashLogService.Record("应用日志上传设置", exception, fatal: false);
            ThemedMessageDialog.Show(this, $"设置已保存，但暂时无法应用到当前连接：{exception.Message}",
                "日志上传设置", MessageKind.Warning);
        }
        RefreshDiagnosticButtons();
        if (window.InstallUpdateRequested)
        {
            if ((!_edgeController.IsRunning && !_starting && !_connectionBusy) || ThemedMessageDialog.Confirm(this,
                    "更新时会先断开虚拟局域网，完成后程序会自动重新打开，再点一次连接即可。",
                    "现在更新吗？", "立即更新", "稍后"))
            {
                await InstallUpdateAsync();
            }
        }
        else
        {
            TryPromptUpdate();
        }
    }

    private void UpdateReady_Click(object sender, RoutedEventArgs e)
    {
        if (!FocusOpenDialog()) TryPromptUpdate(userRequested: true);
    }

    /// <summary>
    /// Asks once per staged version while the player is using the main window. The
    /// ready action remains available after postponing or skipping the question.
    /// </summary>
    private void TryPromptUpdate(bool userRequested = false)
    {
        var ready = _updates.Stage == UpdateStage.Ready && _updates.Available is not null;
        UpdateReadyButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        VersionBadgeBorder.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        UpdateReadyButton.ToolTip = ready ? $"当前版本 {BuildIdentity.Version}；{_updates.Available!.Version} 已准备好，点击选择更新时间。" : null;
        if (_closeInProgress || _showPeerAssistRequests || _updates.Stage != UpdateStage.Ready || _updates.Available is not { } release ||
            (!userRequested && (release.Version == _promptedUpdateVersion || release.Version == _settings.SkippedUpdateVersion)))
        {
            return;
        }
        if (!IsVisible || WindowState == WindowState.Minimized || !IsActive)
        {
            if (_updateTipVersion != release.Version)
            {
                _updateTipVersion = release.Version;
                _trayIcon.ShowUpdateReadyTip(release.Version);
            }
            return;
        }
        // Wait for connection work and existing dialogs rather than interrupting them.
        if (_connectionBusy || _starting || _disconnecting ||
            _lastSnapshot.State is ConnectionState.Connecting or ConnectionState.Reconnecting ||
            OwnedWindows.OfType<Window>().Any(window => window.IsVisible))
        {
            return;
        }
        _promptedUpdateVersion = release.Version;
        var notes = release.Notes.Trim();
        if (notes.Length > 360)
        {
            notes = notes[..360].TrimEnd() + "…";
        }
        var restart = _edgeController.IsRunning
            ? "更新时会先断开虚拟局域网，完成后程序会自动重新打开，再点一次连接即可。"
            : "更新完成后程序会自动重新打开。";
        var choice = ThemedMessageDialog.Choose(this,
            string.IsNullOrEmpty(notes) ? restart : $"{notes}\n\n{restart}",
            $"MikuN2N {release.Version} 已准备好", "立即更新", "跳过这个版本", "稍后");
        if (choice == DialogChoice.Primary)
        {
            _ = InstallUpdateAsync();
        }
        else if (choice == DialogChoice.Secondary)
        {
            _settings.SkippedUpdateVersion = release.Version;
            SaveSettings();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (!UpdateService.CanInstallInPlace(out var reason))
        {
            if (ThemedMessageDialog.Confirm(this, reason, "无法自动更新", "打开下载页", "关闭", MessageKind.Warning))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_updates.Available?.PageUrl ?? UpdateService.ReleasesPage)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception exception)
                {
                    ThemedMessageDialog.Show(this, $"无法打开下载页：{exception.Message}", "打开失败", MessageKind.Warning);
                }
            }
            return;
        }
        await ExitApplicationAsync(installUpdate: true);
    }

    internal void ShowFromTray()
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
        KeyPlainBox.IsEnabled = enabled;
        KeyRevealButton.IsEnabled = enabled;
        RememberKeyBox.IsEnabled = enabled;
        // Switching nodes mid-session would leave the running edge on the old address,
        // so the selector follows the rest of the connection inputs.
        NodeBox.IsEnabled = enabled;
        ManageNodesButton.IsEnabled = enabled;
        AddFirstNodeButton.IsEnabled = enabled;
        const string lockedHint = "断开连接后才能修改";
        NicknameBox.ToolTip = enabled ? "朋友列表中显示的名字" : lockedHint;
        KeyBox.ToolTip = enabled ? "朋友之间需使用相同密钥；留空表示不加密" : lockedHint;
        NodeBox.ToolTip = enabled ? "选择要连接的节点" : "断开连接后才能切换节点";
        ManageNodesButton.ToolTip = enabled ? null : "断开连接后才能修改节点";
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
        else if (action == ClosePreference.Exit && _edgeController.IsRunning)
        {
            // A remembered "exit" otherwise drops every friend on a stray click of X.
            var choice = ThemedMessageDialog.Choose(this,
                "退出后虚拟局域网会断开，正在一起玩的朋友会与你断开连接。",
                "要退出 MikuN2N 吗？", "退出", "最小化到托盘");
            if (choice == DialogChoice.Cancel)
            {
                return;
            }
            if (choice == DialogChoice.Secondary)
            {
                action = ClosePreference.MinimizeToTray;
            }
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

    private async Task ExitApplicationAsync(bool installUpdate = false)
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
            StatusTitle.Text = "正在断开并退出…";
            IsEnabled = false;
            _logFlushTimer.Stop();
            _uptimeTimer.Stop();
            _updateTimer.Stop();
            await _edgeController.DisposeAsync();
            await FinishDiagnosticsAsync();
            _trayIcon.Dispose();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("退出流程", exception, fatal: false);
            EdgeController.KillLaunchedEdgeProcesses();
        }

        if (installUpdate)
        {
            InstallUpdateAndRelaunch();
        }

        _allowClose = true;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Runs with n3n-edge already stopped. A failed swap has been rolled back, so the
    /// relaunch brings back whichever version is on disk either way.
    /// </summary>
    private void InstallUpdateAndRelaunch()
    {
        try
        {
            _updates.InstallStaged();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("安装更新", exception, fatal: false);
            ThemedMessageDialog.Show(null, $"更新没有完成，程序已恢复为原来的版本。\n\n{exception.Message}",
                "更新失败", MessageKind.Warning);
        }
        try
        {
            UpdateService.Relaunch();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("更新后重新启动", exception, fatal: false);
        }
    }
}
