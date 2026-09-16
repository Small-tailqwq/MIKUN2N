using System.Net;
using System.Text;
using System.Windows;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MikuN2N;

public partial class NodeEditDialog : Window
{
    private readonly SupernodeNode _original;
    private readonly IReadOnlyCollection<string> _otherNames;

    public SupernodeNode Result { get; private set; }

    public NodeEditDialog(SupernodeNode? node, IEnumerable<string> otherNames)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        // Editing works on a copy so a cancelled dialog leaves the list untouched.
        _original = node ?? new SupernodeNode();
        Result = new SupernodeNode
        {
            Id = _original.Id,
            Name = _original.Name,
            Server = _original.Server,
            Community = _original.Community
        };
        _otherNames = otherNames.ToList();
        if (node is not null)
        {
            HeaderText.Text = "编辑节点";
            Title = "编辑节点";
        }
        NameBox.Text = Result.Name;
        ServerBox.Text = Result.Server;
        CommunityBox.Text = Result.Community;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var server = ServerBox.Text.Trim();
        var community = CommunityBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            Warn("请填写节点名称，它只在本机显示，用来区分多个节点。");
            return;
        }
        if (_otherNames.Any(other => string.Equals(other, name, StringComparison.OrdinalIgnoreCase)))
        {
            Warn("已有同名节点，请换一个名称。");
            return;
        }
        if (string.IsNullOrEmpty(server) || !IsServerAddress(server))
        {
            Warn("服务器地址不正确，应形如 vps.example.com:3076。");
            return;
        }
        if (string.IsNullOrEmpty(community))
        {
            Warn("请填写小组名称，双方必须一致才能进入同一个虚拟局域网。");
            return;
        }
        if (Encoding.UTF8.GetByteCount(community) > 20 || community.Any(char.IsWhiteSpace))
        {
            Warn("小组名称不能包含空格，且最长为 20 个英文字符。");
            return;
        }

        Result.Name = name;
        Result.Server = server;
        Result.Community = community;
        DialogResult = true;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "请检查填写内容", MessageBoxButton.OK, MessageBoxImage.Warning);

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
        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) is UriHostNameType.Dns;
    }
}
