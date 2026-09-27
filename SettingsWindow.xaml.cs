using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace MikuN2N;

public partial class SettingsWindow : Window
{
    /// <summary>Grid row for the node list; keeps the "current node" mark out of the model.</summary>
    private sealed class NodeRow(SupernodeNode node, bool isActive)
    {
        public string Id { get; } = node.Id;
        public string Name { get; } = node.Name;
        public string Server { get; } = node.Server;
        public string Community { get; } = node.Community;
        public bool IsActive { get; } = isActive;
        public string ActiveMark => IsActive ? "●" : string.Empty;
    }

    private readonly ThemePreference _originalTheme;
    private readonly EasterEggManager _easterEggs;
    private readonly EasterEggVisualController _easterEggVisuals;
    private readonly bool _connected;
    private readonly UpdateService _updates;
    private readonly List<SupernodeNode> _nodes;
    private string _activeNodeId;
    private bool _saved;
    private bool _nodesDirty;
    private bool _discardConfirmed;
    private bool _savePromptPending;
    private bool _slotRolling;
    private int _versionClickCount;
    private DateTime _lastVersionClick = DateTime.MinValue;

    public ThemePreference SelectedTheme { get; private set; }
    public ClosePreference SelectedCloseBehavior { get; private set; }
    public int SelectedLogRetentionDays { get; private set; }
    public bool SelectedExperimentalIpv6P2p { get; private set; }
    public bool SelectedAutoCheckUpdates { get; private set; }
    /// <summary>Set when the player chose to restart into a staged update; the owner runs it after saving.</summary>
    public bool InstallUpdateRequested { get; private set; }
    public DiagnosticUploadPreference SelectedDiagnosticUpload { get; private set; }
    public string SelectedDiagnosticUploadTarget { get; private set; } = string.Empty;
    private DiagnosticUploadPreference _originalDiagnosticUpload;
    private string _originalDiagnosticTarget = string.Empty;
    public List<SupernodeNode> EditedNodes { get; private set; } = [];
    public string SelectedNodeId { get; private set; } = string.Empty;

    public SettingsWindow(AppSettings settings, int initialTabIndex = 1, bool connected = false)
    {
        InitializeComponent();
        MainWindow.FitToWorkArea(this);
        Icon = AppIconService.Icon;
        _easterEggs = ((App)Application.Current).EasterEggs;
        _easterEggVisuals = new EasterEggVisualController(this, EasterEggOverlay, _easterEggs);
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        _originalTheme = settings.Theme;
        SelectedTheme = settings.Theme;
        SelectedCloseBehavior = settings.CloseBehavior;
        SelectedLogRetentionDays = settings.LogRetentionDays;
        SelectedExperimentalIpv6P2p = settings.ExperimentalIpv6P2p;
        _originalDiagnosticUpload = settings.DiagnosticUpload;
        _originalDiagnosticTarget = settings.DiagnosticUploadTarget;
        DiagnosticUploadBox.SelectedValue = settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow &&
            TestBuildProfile.Current is { } consentProfile && !DiagnosticUploadConsent.IsAllowed(settings, consentProfile)
                ? "Ask" : Enum.IsDefined(settings.DiagnosticUpload) ? settings.DiagnosticUpload.ToString() : "Ask";
        DiagnosticUploadBox.IsEnabled = TestBuildProfile.Current is not null;
        // Public builds upload nothing; showing the card there only suggests they do.
        DiagnosticCard.Visibility = TestBuildProfile.Current is null ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticDestinationText.Text = TestBuildProfile.Current is { } diagnosticProfile
            ? $"接收服务器：{new Uri(diagnosticProfile.UploadUrl).Authority}。授权绑定当前节点、接收地址及证书；编辑或切换节点后须重新授权。"
            : "此版本未配置诊断接收端，日志仅保存在本机。";
        ExperimentalIpv6Box.IsChecked = settings.ExperimentalIpv6P2p;
        SelectedAutoCheckUpdates = settings.AutoCheckUpdates;
        AutoCheckUpdatesBox.IsChecked = settings.AutoCheckUpdates;
        _updates = ((App)Application.Current).Updates;
        UpdateCard.Visibility = AboutUpdateCard.Visibility =
            _updates.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        SourceCodeButton.Tag = UpdateService.Repository is { } repository ? $"https://github.com/{repository}" : null;
        SourceCodeButton.Visibility = UpdateService.Repository is null ? Visibility.Collapsed : Visibility.Visible;
        _updates.StateChanged += Updates_StateChanged;
        Closed += (_, _) => _updates.StateChanged -= Updates_StateChanged;
        RefreshUpdateStatus();
        _connected = connected;
        _nodes = settings.Nodes
            .Select(node => new SupernodeNode
            {
                Id = node.Id,
                Name = node.Name,
                Server = node.Server,
                Community = node.Community
            })
            .ToList();
        _activeNodeId = settings.ActiveNode?.Id ?? string.Empty;
        ThemeSystemButton.IsChecked = settings.Theme == ThemePreference.System;
        ThemeLightButton.IsChecked = settings.Theme == ThemePreference.Light;
        ThemeDarkButton.IsChecked = settings.Theme == ThemePreference.Dark;
        CloseAskButton.IsChecked = settings.CloseBehavior == ClosePreference.Ask;
        CloseTrayButton.IsChecked = settings.CloseBehavior == ClosePreference.MinimizeToTray;
        CloseExitButton.IsChecked = settings.CloseBehavior == ClosePreference.Exit;
        LogRetentionBox.SelectedValue = settings.LogRetentionDays.ToString();
        if (LogRetentionBox.SelectedIndex < 0)
        {
            LogRetentionBox.SelectedValue = "30";
        }
        AboutVersionText.Text = $"版本 {BuildIdentity.Version} · Windows x64";
        SettingsTabs.SelectedIndex = Math.Clamp(initialTabIndex, 0, SettingsTabs.Items.Count - 1);
        RefreshNodes();
        Closing += SettingsWindow_Closing;
    }

    /// <summary>
    /// Node edits only take effect on Save; a first-time player who adds a node and
    /// closes the window would otherwise lose it without a word.
    /// </summary>
    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_saved && _nodesDirty && !_discardConfirmed)
        {
            e.Cancel = true;
            if (_savePromptPending)
            {
                return;
            }
            _savePromptPending = true;
            // Save/Close cannot run while this Closing event is on the stack.
            Dispatcher.BeginInvoke(() =>
            {
                _savePromptPending = false;
                var choice = ThemedMessageDialog.Choose(this,
                    "你添加或修改了节点，但还没有保存。不保存的话，这些改动会丢失。",
                    "保存对节点的修改吗？", "保存", "不保存");
                if (choice == DialogChoice.Primary)
                {
                    Save_Click(this, new RoutedEventArgs());
                }
                else if (choice == DialogChoice.Secondary)
                {
                    _discardConfirmed = true;
                    Close();
                }
            });
            return;
        }
        if (!_saved)
        {
            ((App)Application.Current).ThemeManager.Apply(_originalTheme);
        }
    }

    private SupernodeNode? SelectedNode =>
        NodesGrid.SelectedItem is NodeRow row
            ? _nodes.FirstOrDefault(node => node.Id == row.Id)
            : null;

    private void RefreshNodes(string? selectId = null)
    {
        var wanted = selectId ?? SelectedNode?.Id;
        NodesGrid.ItemsSource = null;
        NodesGrid.ItemsSource = _nodes
            .Select(node => new NodeRow(node, node.Id == _activeNodeId))
            .ToList();
        if (wanted is not null)
        {
            NodesGrid.SelectedItem = (NodesGrid.ItemsSource as IEnumerable<NodeRow>)?
                .FirstOrDefault(row => row.Id == wanted);
        }
        if (NodesGrid.SelectedItem is null && NodesGrid.Items.Count > 0)
        {
            NodesGrid.SelectedIndex = 0;
        }
        UpdateNodeButtons();
        NodesHintText.Text = _nodes.Count == 0
            ? "还没有节点。点击“添加…”，填入你自己搭建或朋友分享的服务器地址。"
            : _connected
                ? "当前正在连接中，修改当前节点的地址或小组名称需要先断开连接。"
                : $"共 {_nodes.Count} 个节点，双击一行可以编辑。";
    }

    private void UpdateNodeButtons()
    {
        var row = NodesGrid.SelectedItem as NodeRow;
        // The running edge keeps using the current node, so it stays locked until disconnect.
        var locked = _connected && row is not null && row.Id == _activeNodeId;
        EditNodeButton.IsEnabled = row is not null && !locked;
        RemoveNodeButton.IsEnabled = row is not null && !locked;
        UseNodeButton.IsEnabled = row is not null && row.Id != _activeNodeId && !_connected;
        const string lockedHint = "正在使用这个节点联机，请先在主界面断开连接";
        EditNodeButton.ToolTip = locked ? lockedHint : null;
        RemoveNodeButton.ToolTip = locked ? lockedHint : null;
        UseNodeButton.ToolTip = _connected ? "断开连接后才能切换当前节点" : null;
    }

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateNodeButtons();

    private void NodesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateNodeButtons();

    private void NodesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking a header or the scrollbar must not open the editor.
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(NodesGrid, source) is DataGridRow &&
            EditNodeButton.IsEnabled)
        {
            EditNode_Click(sender, e);
        }
    }

    private void AddNode_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NodeEditDialog(null, _nodes.Select(node => node.Name)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        _nodes.Add(dialog.Result);
        if (string.IsNullOrEmpty(_activeNodeId))
        {
            _activeNodeId = dialog.Result.Id;
        }
        _nodesDirty = true;
        RefreshNodes(dialog.Result.Id);
    }

    private void EditNode_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }
        if (_connected && node.Id == _activeNodeId)
        {
            return;
        }
        var dialog = new NodeEditDialog(node, _nodes.Where(other => other.Id != node.Id).Select(other => other.Name))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        node.Name = dialog.Result.Name;
        node.Server = dialog.Result.Server;
        node.Community = dialog.Result.Community;
        if (node.Id == _activeNodeId) InvalidateDiagnosticConsent();
        _nodesDirty = true;
        RefreshNodes(node.Id);
    }

    private void RemoveNode_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }
        if (_connected && node.Id == _activeNodeId)
        {
            return;
        }
        if (!ThemedMessageDialog.Confirm(this, $"删除后需要重新填写地址和小组名称才能再用它联机。",
                $"删除节点“{node.Name}”？", "删除", kind: MessageKind.Warning))
        {
            return;
        }
        _nodes.Remove(node);
        _nodesDirty = true;
        if (node.Id == _activeNodeId)
        {
            InvalidateDiagnosticConsent();
            _activeNodeId = _nodes.Count > 0 ? _nodes[0].Id : string.Empty;
        }
        RefreshNodes();
    }

    private void UseNode_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node is null || _connected)
        {
            return;
        }
        if (_activeNodeId != node.Id) InvalidateDiagnosticConsent();
        _activeNodeId = node.Id;
        _nodesDirty = true;
        RefreshNodes(node.Id);
    }

    private void InvalidateDiagnosticConsent()
    {
        _originalDiagnosticTarget = string.Empty;
        if (DiagnosticUploadBox.SelectedValue?.ToString() == "AlwaysAllow")
            DiagnosticUploadBox.SelectedValue = "Ask";
    }

    /// <summary>
    /// Rejects rows that would fail connection later, so a typo is caught here rather
    /// than in the main window's "请检查连接信息" dialog.
    /// </summary>
    private bool TryValidateNodes(out string error)
    {
        if (_nodes.Count == 0)
        {
            error = string.Empty;
            return true;
        }
        foreach (var node in _nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
            {
                error = "每个节点都需要一个名称，用来在主界面区分它们。";
                return false;
            }
            if (!NodeAddress.TryNormalize(node.Server, out _, out var serverError))
            {
                error = $"节点“{node.Name}”的地址不正确：{serverError}";
                return false;
            }
            if (!NodeAddress.TryValidateCommunity(node.Community, out var communityError))
            {
                error = $"节点“{node.Name}”的小组名称不正确：{communityError}";
                return false;
            }
        }
        if (_nodes.All(node => node.Id != _activeNodeId))
        {
            error = "请选择一个当前要使用的节点。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private void ThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        ((App)Application.Current).ThemeManager.Apply(CurrentTheme());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateNodes(out var nodeError))
        {
            SettingsTabs.SelectedIndex = 0;
            ThemedMessageDialog.Show(this, nodeError, "请检查节点信息", MessageKind.Warning);
            return;
        }

        SelectedTheme = CurrentTheme();
        SelectedDiagnosticUpload = Enum.TryParse<DiagnosticUploadPreference>(DiagnosticUploadBox.SelectedValue?.ToString(), out var upload)
            ? upload : DiagnosticUploadPreference.Ask;
        SelectedDiagnosticUploadTarget = string.Empty;
        if (SelectedDiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow && TestBuildProfile.Current is { } profile)
        {
            var node = _nodes.FirstOrDefault(node => node.Id == _activeNodeId);
            if (node is null) { SelectedDiagnosticUpload = DiagnosticUploadPreference.Ask; }
            var target = DiagnosticUploadConsent.Target(profile, node);
            if ((_originalDiagnosticUpload != DiagnosticUploadPreference.AlwaysAllow || _originalDiagnosticTarget != target) &&
                node is not null && new LogUploadConsentDialog(profile, persistent: true, node: node) { Owner = this }.ShowDialog() != true) return;
            SelectedDiagnosticUploadTarget = target;
        }
        SelectedExperimentalIpv6P2p = ExperimentalIpv6Box.IsChecked == true;
        SelectedAutoCheckUpdates = AutoCheckUpdatesBox.IsChecked == true;
        SelectedCloseBehavior = CloseTrayButton.IsChecked == true
            ? ClosePreference.MinimizeToTray
            : CloseExitButton.IsChecked == true
                ? ClosePreference.Exit
                : ClosePreference.Ask;
        SelectedLogRetentionDays = int.TryParse(
            LogRetentionBox.SelectedValue?.ToString(),
            out var retentionDays)
            ? retentionDays
            : 30;
        EditedNodes = _nodes;
        SelectedNodeId = _activeNodeId;
        _saved = true;
        DialogResult = true;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        TryOpen(() =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikuN2N", "logs");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            TryOpen(() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        }
    }

    private void Updates_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(RefreshUpdateStatus);

    private void RefreshUpdateStatus()
    {
        var version = _updates.Available?.Version;
        (UpdateStatusText.Text, UpdateActionButton.Content, UpdateActionButton.IsEnabled) = _updates.Stage switch
        {
            UpdateStage.Checking => ("正在连接 GitHub 检查新版本…", "检查更新", false),
            UpdateStage.UpToDate => ($"当前版本 {BuildIdentity.Version} 已是最新。", "再次检查", true),
            UpdateStage.Downloading => ($"正在下载 {version}：{_updates.Progress:P0}", "下载中…", false),
            UpdateStage.Ready => ($"{version} 已下载并校验完成，重启程序即可完成更新。", "重启并更新", true),
            UpdateStage.Failed => (_updates.Error ?? "检查更新失败。", "重试", true),
            _ => ($"当前版本 {BuildIdentity.Version}，更新来自 GitHub 发布页。", "检查更新", true)
        };
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Stage != UpdateStage.Ready)
        {
            _ = _updates.CheckAndPrepareAsync();
            return;
        }
        // Restarting goes through Save so other edits in this window are not lost.
        InstallUpdateRequested = true;
        Save_Click(this, new RoutedEventArgs());
        if (!_saved)
        {
            InstallUpdateRequested = false;
        }
    }

    private void OpenBundledLicense_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Runtime", "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(path))
        {
            TryOpen(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        }
    }

    private void CopyVersion_Click(object sender, RoutedEventArgs e) =>
        TryOpen(() => Clipboard.SetText(
            $"MikuN2N {BuildIdentity.Version}\nWindows {Environment.OSVersion.Version}\n.NET {Environment.Version}"));

    /// <summary>
    /// Any exception reaching the dispatcher shuts the program down and drops the
    /// connection, which a busy clipboard or missing file association must not cause.
    /// </summary>
    private void TryOpen(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ThemedMessageDialog.Show(this, $"操作没有完成：{exception.Message}", "请稍后重试", MessageKind.Warning);
        }
    }

    private async void AboutVersionText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        _versionClickCount = now - _lastVersionClick > TimeSpan.FromSeconds(1.25)
            ? 1
            : _versionClickCount + 1;
        _lastVersionClick = now;
        if (_versionClickCount < 7 || _slotRolling)
        {
            return;
        }

        _versionClickCount = 0;
        await RollSlotAsync();
    }

    private async Task RollSlotAsync()
    {
        _slotRolling = true;
        EasterEggResultText.Visibility = Visibility.Collapsed;
        try
        {
            for (var step = 0; step < 30; step++)
            {
                SetSlot(RandomLetter(), Random.Shared.Next(0, 10), RandomLetter());
                await Task.Delay(38 + step * 3);
            }

            string result;
            if (_easterEggs.ShouldAwardJackpot())
            {
                result = Random.Shared.Next(0, 2) == 0 ? "N2N" : "N3N";
                SetSlot(result[0], result[1] - '0', result[2]);
                EasterEggResultText.Text = $"JACKPOT · {result} · 彩虹协议已加载";
                EasterEggResultText.Visibility = Visibility.Visible;
                _easterEggs.ActivateJackpot(result);
            }
            else
            {
                do
                {
                    result = $"{RandomLetter()}{Random.Shared.Next(0, 10)}{RandomLetter()}";
                }
                while (result is "N2N" or "N3N");

                SetSlot(result[0], result[1] - '0', result[2]);
                if (Random.Shared.Next(0, 3) == 0)
                {
                    _easterEggs.AddFly();
                    EasterEggResultText.Text = $"{result} · 蓝色苍蝇 +1（当前 {_easterEggs.FlyCount}）";
                }
                else
                {
                    _easterEggs.AddSpider();
                    EasterEggResultText.Text = $"{result} · 蜘蛛 +1（当前 {_easterEggs.SpiderCount}）";
                }
                EasterEggResultText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _slotRolling = false;
        }
    }

    private void SetSlot(char first, int digit, char last)
    {
        SlotFirstText.Text = first.ToString();
        SlotDigitText.Text = digit.ToString();
        SlotLastText.Text = last.ToString();
    }

    private static char RandomLetter() => (char)('A' + Random.Shared.Next(0, 26));

    private ThemePreference CurrentTheme() => ThemeLightButton.IsChecked == true
        ? ThemePreference.Light
        : ThemeDarkButton.IsChecked == true
            ? ThemePreference.Dark
            : ThemePreference.System;
}
