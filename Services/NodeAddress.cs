using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MikuN2N.Services;

/// <summary>Node field validation shared by the node dialog, the settings list and the connect button.</summary>
public static partial class NodeAddress
{
    public const int MaxCommunityBytes = 20;
    public const int MaxNicknameBytes = 31;

    /// <summary>
    /// Accepts what players paste from chat - a full-width colon, a scheme prefix or a
    /// trailing slash - and explains which part of which endpoint is wrong.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        var tokens = EdgeController.SplitServers((value ?? string.Empty).Replace('\uFF1A', ':'))
            .Select(token => SchemePrefix().Replace(token, string.Empty).TrimEnd('/'))
            .Where(token => token.Length > 0)
            .ToList();
        if (tokens.Count == 0)
        {
            error = "请填写服务器地址，例如 vps.example.com:3076。";
            return false;
        }
        foreach (var token in tokens)
        {
            if (!TryValidateEndpoint(token, out error))
            {
                return false;
            }
        }
        normalized = string.Join(", ", tokens);
        error = string.Empty;
        return true;
    }

    public static bool IsValid(string? value) => TryNormalize(value, out _, out _);

    public static bool TryValidateCommunity(string? value, out string error)
    {
        var community = value?.Trim() ?? string.Empty;
        error = community.Length == 0
            ? "请填写小组名称，双方必须一致才能进入同一个虚拟局域网。"
            : community.Any(char.IsWhiteSpace)
                ? "小组名称不能包含空格。"
                : Encoding.UTF8.GetByteCount(community) > MaxCommunityBytes
                    ? $"小组名称太长：最多 {MaxCommunityBytes} 个英文字符（一个汉字约占 3 个）。"
                    : string.Empty;
        return error.Length == 0;
    }

    private static bool TryValidateEndpoint(string token, out string error)
    {
        var separator = token.LastIndexOf(':');
        if (separator <= 0 || separator == token.Length - 1 || token.EndsWith(']'))
        {
            error = $"“{token}”缺少端口号，应形如 vps.example.com:3076。";
            return false;
        }
        if (!int.TryParse(token[(separator + 1)..], out var port) || port is < 1 or > 65535)
        {
            error = $"“{token}”的端口应是 1–65535 之间的数字。";
            return false;
        }
        var host = token[..separator];
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) is not UriHostNameType.Dns)
        {
            error = $"“{host}”不是有效的域名或 IP 地址。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex SchemePrefix();
}
