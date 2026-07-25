using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

using System.Threading;
using System.Threading.Tasks;

namespace MikuN2N.Services;

/// <summary>
/// Makes the virtual adapter behave like a real LAN card.
///
/// Windows gets in the way in three separate places, and a player hits all three:
/// the TAP adapter is filed as a public network so inbound traffic is dropped, no
/// rule allows ICMP echo so nobody can even ping, and - the one that silently
/// breaks game room discovery - 224.0.0.0/4 and 255.255.255.255/32 exist on every
/// interface with the same route metric, so the tie is settled by the interface
/// metric and the physical NIC wins. Broadcasts then leave through the real network
/// card and never enter the tunnel.
/// </summary>
internal static class NetworkTuningService
{
    private const string FirewallRuleName = "MikuN2N 虚拟局域网";

    /// <summary>
    /// Interface metric for the tunnel. n3n already asks for this through its own
    /// config; this is the fallback for when that did not take, and the value has to
    /// stay below every physical adapter's metric to win the broadcast tie-break.
    /// The adapter carries no default route, so a low metric cannot capture internet
    /// traffic - route lookup still picks the most specific prefix.
    /// </summary>
    private const int TunnelMetric = 1;

    public static async Task ApplyAsync(
        string? adapterId,
        IPAddress address,
        IPAddress subnetMask,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        var index = ResolveInterfaceIndex(adapterId, address);
        if (index is null)
        {
            return;
        }

        // netsh wants the network base, not the host address we happen to hold.
        var scope = $"{ToNetworkAddress(address, subnetMask)}/{ToPrefixLength(subnetMask)}";
        await EnsureFirewallRuleAsync(scope, log, cancellationToken);
        await EnsurePrivateProfileAsync(index.Value, log, cancellationToken);
        await EnsureBroadcastPriorityAsync(index.Value, log, cancellationToken);
    }

    /// <summary>
    /// Hands the interface metric back to Windows. n3n restores it on a clean exit,
    /// but it is killed or crashes often enough that relying on that would leave a
    /// stale metric=1 on an idle adapter - which would then win the broadcast
    /// tie-break against the user's real network card while MikuN2N is not even
    /// running.
    /// </summary>
    public static async Task RestoreAsync(
        string? adapterId,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        var index = ResolveInterfaceIndex(adapterId, network: null);
        if (index is null)
        {
            return;
        }

        try
        {
            await RunAsync(
                "netsh",
                ["interface", "ipv4", "set", "interface", index.Value.ToString(), "metric=automatic"],
                cancellationToken);
        }
        catch (Exception exception)
        {
            log($"网卡优化：恢复网卡跃点失败：{exception.Message}");
        }
    }

    private static async Task EnsureFirewallRuleAsync(
        string scope,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            // The rule is rewritten rather than probed: the virtual subnet is fixed
            // today but a changed scope has to replace the old rule, not add a second.
            await RunAsync(
                "netsh",
                ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"],
                cancellationToken);
            var (exitCode, output) = await RunAsync(
                "netsh",
                [
                    "advfirewall", "firewall", "add", "rule",
                    $"name={FirewallRuleName}",
                    "dir=in",
                    "action=allow",
                    "protocol=any",
                    $"remoteip={scope}",
                    "profile=any",
                    "description=允许来自 MikuN2N 虚拟局域网内好友的连接（联机、房间发现、ping）。删除本规则会恢复 Windows 默认拦截。"
                ],
                cancellationToken);
            log(exitCode == 0
                ? $"网卡优化：已允许来自虚拟局域网 {scope} 的入站连接（联机、房间发现、ping）。"
                : $"网卡优化：防火墙放行规则添加失败：{output}");
        }
        catch (Exception exception)
        {
            log($"网卡优化：设置防火墙放行规则时出错：{exception.Message}");
        }
    }

    private static async Task EnsurePrivateProfileAsync(
        int index,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output) = await RunAsync(
                "powershell",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    $"Set-NetConnectionProfile -InterfaceIndex {index} -NetworkCategory Private -ErrorAction Stop"
                ],
                cancellationToken);
            if (exitCode == 0)
            {
                log("网卡优化：已将虚拟网卡设为“专用网络”，局域网发现不再被系统拦截。");
            }
            else
            {
                log($"网卡优化：设置“专用网络”未成功：{FirstLine(output)}");
            }
        }
        catch (Exception exception)
        {
            log($"网卡优化：设置“专用网络”时出错：{exception.Message}");
        }
    }

    private static async Task EnsureBroadcastPriorityAsync(
        int index,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            // Setting it unconditionally: n3n normally applies this through its own
            // config, and re-applying the same value costs nothing, whereas reading the
            // current metric back has no managed API and would need another netsh call.
            var (exitCode, output) = await RunAsync(
                "netsh",
                ["interface", "ipv4", "set", "interface", index.ToString(), $"metric={TunnelMetric}"],
                cancellationToken);
            log(exitCode == 0
                ? "网卡优化：已提升虚拟网卡优先级，局域网游戏的房间广播将走隧道。"
                : $"网卡优化：调整网卡优先级未成功：{FirstLine(output)}");
        }
        catch (Exception exception)
        {
            log($"网卡优化：调整网卡优先级时出错：{exception.Message}");
        }
    }

    private static int? ResolveInterfaceIndex(string? adapterId, IPAddress? network)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapterId is not null &&
                    !string.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var properties = adapter.GetIPProperties();
                if (adapterId is null &&
                    (network is null ||
                     !properties.UnicastAddresses.Any(item => item.Address.Equals(network))))
                {
                    continue;
                }
                return properties.GetIPv4Properties().Index;
            }
            catch (Exception)
            {
                // Adapters without IPv4 properties are never the tunnel.
            }
        }
        return null;
    }

    private static IPAddress ToNetworkAddress(IPAddress address, IPAddress subnetMask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        if (addressBytes.Length != maskBytes.Length)
        {
            return address;
        }
        for (var i = 0; i < addressBytes.Length; i++)
        {
            addressBytes[i] &= maskBytes[i];
        }
        return new IPAddress(addressBytes);
    }

    private static int ToPrefixLength(IPAddress subnetMask)
    {
        var bits = 0;
        foreach (var b in subnetMask.GetAddressBytes())
        {
            bits += System.Numerics.BitOperations.PopCount(b);
        }
        return bits;
    }

    private static string FirstLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
        ?? string.Empty;

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
            // Deliberately no explicit encoding: netsh writes in the console OEM code
            // page (936 here), so forcing UTF-8 would turn every diagnostic we log
            // into mojibake.
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
