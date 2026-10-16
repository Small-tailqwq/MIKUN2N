using System.Windows;
using System.Windows.Controls;
using MikuN2N.Models;

namespace MikuN2N;

public partial class MainWindow
{
    private void RefreshIpv6Indicator(ConnectionSnapshot snapshot)
    {
        var view = DescribeIpv6(snapshot, _settings.ExperimentalIpv6P2p);
        Ipv4LabelText.Visibility = Ipv6StatusPanel.Visibility = view.Visible ? Visibility.Visible : Visibility.Collapsed;
        NatTypeText.FontSize = view.Visible ? 18 : 21;
        Ipv6TypeText.Text = view.Title;
        Ipv6ConditionText.Text = view.Detail;
        Ipv6StatusPanel.ToolTip = view.Tooltip;
        Ipv6HealthDot.SetResourceReference(TextBlock.ForegroundProperty, view.Tone);
    }

    internal sealed record Ipv6Presentation(bool Visible, string Title, string Detail, string Tooltip, string Tone = "TextMuted");

    internal static Ipv6Presentation DescribeIpv6(ConnectionSnapshot snapshot, bool enabled)
    {
        if (!enabled) return new(false, "", "", "");
        if (snapshot.State is ConnectionState.Disconnected or ConnectionState.Error)
            return new(true, "待连接", "连接后自动检测", "IPv6 设置在下次连接时生效。");
        if (snapshot.State is ConnectionState.Connecting or ConnectionState.Reconnecting)
            return new(true, "检测中", "等待连接就绪", "连接就绪后读取现有 IPv6 探测结果。");
        var status = snapshot.Ipv6;
        if (status is { Requested: false })
            return new(true, "待启用", "下次连接生效", "当前连接未请求 IPv6；下次连接会按已保存的设置启用。");
        const string verified = "已有地址探测或好友往返验证；不代表能与所有好友建立 IPv6 直连。";
        return status?.Availability switch
        {
            Ipv6Availability.Available when status.Mapping == Ipv6MappingKind.AddressTranslated =>
                new(true, "可用", "NAT66 / NPTv6", "现有地址探测确认源 IPv6 地址发生转换，无法进一步区分 NAT66 与 NPTv6。" + verified, "Success"),
            Ipv6Availability.Available when status.Mapping == Ipv6MappingKind.PortTranslated =>
                new(true, "可用", "端口转换", "现有地址探测确认 UDP 端口发生转换，IPv6 地址未变。" + verified, "Success"),
            Ipv6Availability.Available when status.Mapping == Ipv6MappingKind.Untranslated && status.AddressKind == Ipv6AddressKind.Global =>
                new(true, "可用", "公网地址 · 已验证", "本次地址探测的外部端点与本地端点一致，未发现地址或端口转换。" + verified, "Success"),
            Ipv6Availability.Available =>
                new(true, "可用", "往返已验证 · 类型未知", verified + "尚无有效的地址转换检测结果。", "Success"),
            Ipv6Availability.Unavailable =>
                new(true, "不可用", "当前继续使用 IPv4", "尚无可用的 IPv6 传输地址或近期探测持续失败；程序会沿用原有 IPv4 线路。", "Warning"),
            Ipv6Availability.Unsupported =>
                new(true, "状态未报告", "当前运行时未提供结果", "旧版 edge 可能不支持 IPv6，或没有上报相关字段；不会据此认定网络不可用。"),
            Ipv6Availability.Disabled =>
                new(true, "未启用", "当前继续使用 IPv4", "当前 edge 报告 IPv6 开关未启用。"),
            Ipv6Availability.Unknown =>
                new(true, "状态待确认", "等待最新检测结果", "暂未获取到有效的最新状态，不沿用过期的可用结论。"),
            Ipv6Availability.Unverified when status.AddressKind == Ipv6AddressKind.Global =>
                new(true, "待验证", "有全局地址 · 连通未确认", "仅有全局 IPv6 地址不能证明公网可达；等待已有地址探测或好友往返验证。"),
            Ipv6Availability.Unverified when status.AddressKind == Ipv6AddressKind.UniqueLocal =>
                new(true, "待验证", "本地地址 · 映射未确认", "已有 ULA 地址，尚无已验证的外部映射；不能仅凭 ULA 地址认定 NAT66。"),
            Ipv6Availability.Unverified =>
                new(true, "待验证", "尚无连通性结果", "IPv6 已开启，但当前运行时尚未上报有效的连通性结果。"),
            _ => new(true, "检测中", "正在确认 IPv6 连通性", "复用现有 edge 的地址与好友探测，不增加额外扫描。")
        };
    }
}
