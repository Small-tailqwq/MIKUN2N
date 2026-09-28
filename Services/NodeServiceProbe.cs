using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MikuN2N.Services;

/// <summary>
/// Optional services a MikuN2N supernode host may offer next to the supernode itself:
/// the IPv6 mapping observer (UDP 3478) and the diagnostics receiver (TCP 5443).
/// </summary>
public static class NodeServiceProbe
{
    public const int ObserverPort = 3478;
    // Public fallback observer (Cloudflare, UDP 3478 over IPv6). It receives only the
    // anonymous 20-byte STUN request, never the MikuN2N tag.
    public const string FallbackObserverHost = "stun.cloudflare.com";
    private const uint Cookie = 0x2112A442;

    /// <summary>Host of the node's first supernode endpoint, without port or brackets.</summary>
    public static string? PrimaryHost(string server)
    {
        var entry = EdgeController.SplitServers(server).FirstOrDefault();
        if (string.IsNullOrEmpty(entry)) return null;
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']');
            return close > 1 ? entry[1..close] : null;
        }
        var colon = entry.LastIndexOf(':');
        return colon > 0 ? entry[..colon] : entry;
    }

    /// <summary>SHA-256 of the receiver's certificate, or null when nothing answers.</summary>
    public static async Task<string?> FetchReceiverCertificateAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, TestBuildProfile.NodeReceiverPort, timeout.Token);
            string? fingerprint = null;
            // The receiver uses a self-signed certificate; its fingerprint is shown to the
            // player for consent and pinned afterwards, so no chain validation applies here.
            await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            {
                fingerprint = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
                return certificate is not null;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
            return fingerprint;
        }
        catch (Exception exception) when (exception is SocketException or IOException or
                                           OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// Asks the supernode host over IPv4 whether it runs the MikuN2N observer and at
    /// which global IPv6 address. Null means the host offers none.
    /// </summary>
    public static async Task<IPAddress?> DiscoverObserverAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            IPAddress? target = IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork
                ? literal
                : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken)).FirstOrDefault();
            if (target is null) return null;
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var transaction = RandomNumberGenerator.GetBytes(12);
            var request = BuildDiscoverRequest(transaction);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await udp.SendAsync(request, new IPEndPoint(target, ObserverPort), cancellationToken);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromMilliseconds(1200));
                try
                {
                    while (true)
                    {
                        var reply = await udp.ReceiveAsync(wait.Token);
                        if (reply.RemoteEndPoint.Address.Equals(target) &&
                            ParseObserverAddress(reply.Buffer, transaction) is { } observer)
                            return observer;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public static async Task<IPAddress?> ResolveFallbackObserverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(FallbackObserverHost, AddressFamily.InterNetworkV6, cancellationToken);
            return addresses.FirstOrDefault(IsGlobalIpv6);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public static bool IsGlobalIpv6(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xe0) == 0x20;

    // The request is as long as the answer (XOR-MAPPED-ADDRESS plus OTHER-ADDRESS), so
    // the observer never sends more bytes than it receives.
    internal static byte[] BuildDiscoverRequest(byte[] transaction)
    {
        var software = Encoding.ASCII.GetBytes("MikuN2N/1 discover".PadRight(32));
        var request = new byte[20 + 4 + software.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)(4 + software.Length));
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), Cookie);
        transaction.CopyTo(request, 8);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20), 0x8022);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22), (ushort)software.Length);
        software.CopyTo(request, 24);
        return request;
    }

    internal static IPAddress? ParseObserverAddress(byte[] reply, byte[] transaction)
    {
        if (reply.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(reply) != 0x0101 ||
            BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) != Cookie ||
            !reply.AsSpan(8, 12).SequenceEqual(transaction) ||
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)) + 20 != reply.Length)
            return null;
        for (var offset = 20; offset + 4 <= reply.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(offset + 2));
            var value = offset + 4;
            if (value + length > reply.Length) return null;
            // OTHER-ADDRESS carries the observer's IPv6 address (family 2) unmasked.
            if (type == 0x802C && length == 20 && reply[value + 1] == 2)
            {
                var address = new IPAddress(reply.AsSpan(value + 4, 16));
                return IsGlobalIpv6(address) ? address : null;
            }
            offset = value + ((length + 3) & ~3);
        }
        return null;
    }
}
