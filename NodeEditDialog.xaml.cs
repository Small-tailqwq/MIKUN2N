using System.Windows;
using System.Windows.Controls;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;

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

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        var error = sender == NameBox ? NameError : sender == ServerBox ? ServerError : CommunityError;
        error.Visibility = Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var community = CommunityBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            ShowError(NameBox, NameError, "请填写节点名称，它只在本机显示，用来区分多个节点。");
            return;
        }
        if (_otherNames.Any(other => string.Equals(other, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(NameBox, NameError, "已有同名节点，请换一个名称。");
            return;
        }
        if (!NodeAddress.TryNormalize(ServerBox.Text, out var server, out var serverError))
        {
            ShowError(ServerBox, ServerError, serverError);
            return;
        }
        if (!NodeAddress.TryValidateCommunity(community, out var communityError))
        {
            ShowError(CommunityBox, CommunityError, communityError);
            return;
        }

        Result.Name = name;
        Result.Server = server;
        Result.Community = community;
        DialogResult = true;
    }

    private static void ShowError(TextBox field, TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        field.Focus();
        field.SelectAll();
    }
}
