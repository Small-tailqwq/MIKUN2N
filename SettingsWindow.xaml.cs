using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

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
    private readonly List<SupernodeNode> _nodes;
    private string _activeNodeId;
    private bool _saved;
    private bool _slotRolling;
    private int _versionClickCount;
    private DateTime _lastVersionClick = DateTime.MinValue;

    public ThemePreference SelectedTheme { get; private set; }
    public ClosePreference SelectedCloseBehavior { get; private set; }
    public int SelectedLogRetentionDays { get; private set; }
    public bool SelectedExperimentalIpv6P2p { get; private set; }
    public List<SupernodeNode> EditedNodes { get; private set; } = [];
    public string SelectedNodeId { get; private set; } = string.Empty;

    public SettingsWindow(AppSettings settings, int initialTabIndex = 1, bool connected = false)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        _easterEggs = ((App)Application.Current).EasterEggs;
        _easterEggVisuals = new EasterEggVisualController(this, EasterEggOverlay, _easterEggs);
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        _originalTheme = settings.Theme;
        SelectedTheme = settings.Theme;
        SelectedCloseBehavior = settings.CloseBehavior;
        SelectedLogRetentionDays = settings.LogRetentionDays;
        SelectedExperimentalIpv6P2p = settings.ExperimentalIpv6P2p;
        ExperimentalIpv6Box.IsChecked = settings.ExperimentalIpv6P2p;
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
        Closing += (_, _) =>
        {
            if (!_saved)
            {
                ((App)Application.Current).ThemeManager.Apply(_originalTheme);
            }
        };
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
            ? "还没有节点。点击“添加…”填入你自己搭建的 supernode 地址。"
            : _connected
                ? "当前正在连接中，修改当前节点的地址或小组名称需要先断开连接。"
                : $"共 {_nodes.Count} 个节点，双击一行可以编辑。";
    }

    private void UpdateNodeButtons()
    {
        var row = NodesGrid.SelectedItem as NodeRow;
        EditNodeButton.IsEnabled = row is not null;
        RemoveNodeButton.IsEnabled = row is not null;
        UseNodeButton.IsEnabled = row is not null && row.Id != _activeNodeId;
    }

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateNodeButtons();

    private void NodesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateNodeButtons();

    private void NodesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditNode_Click(sender, e);

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
            MessageBox.Show(
                this,
                "当前节点正在使用中。请先在主界面断开连接，再修改它的地址或小组名称。",
                "节点正在使用",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
            MessageBox.Show(
                this,
                "不能删除正在使用的节点。请先在主界面断开连接。",
                "节点正在使用",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                this,
                $"确定删除节点“{node.Name}”吗？",
                "删除节点",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _nodes.Remove(node);
        if (node.Id == _activeNodeId)
        {
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
        _activeNodeId = node.Id;
        RefreshNodes(node.Id);
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
            if (!IsServerAddress(node.Server))
            {
                error = $"节点“{node.Name}”的地址不正确，应形如 vps.example.com:3076。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(node.Community) ||
                System.Text.Encoding.UTF8.GetByteCount(node.Community.Trim()) > 20 ||
                node.Community.Any(char.IsWhiteSpace))
            {
                error = $"节点“{node.Name}”的小组名称不能为空、不能包含空格，且最长为 20 个英文字符。";
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

    private static bool IsServerAddress(string value)
    {
        var tokens = value.Split(
            [',', ';', '、', '，', '；', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(IsServerToken);
    }

    private static bool IsServerToken(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > 65535)
        {
            return false;
        }
        var host = value[..separator];
        return System.Net.IPAddress.TryParse(host, out _) ||
               Uri.CheckHostName(host) is UriHostNameType.Dns;
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
            MessageBox.Show(this, nodeError, "请检查节点信息", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedTheme = CurrentTheme();
        SelectedExperimentalIpv6P2p = ExperimentalIpv6Box.IsChecked == true;
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikuN2N", "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void OpenBundledLicense_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Runtime", "LICENSE-n2n.txt");
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void CopyVersion_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText($"MikuN2N {BuildIdentity.Version}\nWindows {Environment.OSVersion.Version}\n.NET {Environment.Version}");

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
