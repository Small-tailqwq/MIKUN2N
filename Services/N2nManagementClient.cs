using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MikuN2N.Services;

public enum ManagementProtocol
{
    N2nUdp,
    N3nHttp
}

public sealed class N2nManagementClient : IDisposable
{
    private readonly int _port;
    private readonly string _password;
    private readonly ManagementProtocol _protocol;
    private readonly HttpClient? _httpClient;
    private int _tag;

    public N2nManagementClient(int port, string password, ManagementProtocol protocol)
    {
        _port = port;
        _password = password;
        _protocol = protocol;
        if (protocol == ManagementProtocol.N3nHttp)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                // The listener is IPv6 loopback-only. System proxy bypass lists
                // commonly include localhost and 127.0.0.1 but omit ::1.
                UseProxy = false
            })
            {
                // n3n 3.4.x binds its Windows management listener to IPv6 loopback only.
                BaseAddress = new Uri($"http://[::1]:{port}/")
            };
        }
    }

    public Task<IReadOnlyList<JsonElement>> GetSupernodesAsync(CancellationToken cancellationToken) =>
        _protocol == ManagementProtocol.N3nHttp
            ? CallJsonRpcAsync("get_supernodes", authenticated: false, cancellationToken)
            : CallLegacyAsync('r', "supernodes", cancellationToken);

    public Task<IReadOnlyList<JsonElement>> GetEdgesAsync(CancellationToken cancellationToken) =>
        _protocol == ManagementProtocol.N3nHttp
            ? CallJsonRpcAsync("get_edges", authenticated: false, cancellationToken)
            : CallLegacyAsync('r', "edges", cancellationToken);

    public Task<IReadOnlyList<JsonElement>> GetNatAsync(CancellationToken cancellationToken) =>
        _protocol == ManagementProtocol.N3nHttp
            ? CallJsonRpcAsync("get_nat", authenticated: false, cancellationToken)
            : Task.FromResult<IReadOnlyList<JsonElement>>([]);

    /// <returns>
    /// How many peer entries the edge actually updated. Zero means the policy was
    /// recorded but no known/pending peer carries that virtual address yet.
    /// </returns>
    /// <param name="edgeMac">
    /// The peer's n3n MAC when known. n3n only learns a peer's virtual IPv4 from a
    /// REGISTER, so a peer first seen through a data packet has none and an
    /// IPv4-only request would never reach it; the MAC always identifies it.
    /// </param>
    public async Task<int> SetPeerRelayAsync(
        string virtualIp,
        bool forceRelay,
        CancellationToken cancellationToken,
        string? edgeMac = null)
    {
        if (_protocol != ManagementProtocol.N3nHttp)
        {
            throw new NotSupportedException("当前 edge 版本不支持按用户切换 pSp 中继。");
        }
        var result = await SendJsonRpcAsync(
            "set_peer_relay",
            authenticated: true,
            cancellationToken,
            edgeMac is null
                ? new object[] { virtualIp, forceRelay }
                : new object[] { virtualIp, forceRelay, edgeMac });
        return result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var updated)
            ? updated
            : 0;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_protocol == ManagementProtocol.N3nHttp)
        {
            _ = await CallJsonRpcAsync("stop", authenticated: true, cancellationToken);
            return;
        }
        _ = await CallLegacyAsync('w', "stop", cancellationToken);
    }

    public void Dispose() => _httpClient?.Dispose();

    private async Task<IReadOnlyList<JsonElement>> CallJsonRpcAsync(
        string method,
        bool authenticated,
        CancellationToken cancellationToken,
        object? parameters = null)
    {
        var result = await SendJsonRpcAsync(method, authenticated, cancellationToken, parameters);
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [];
    }

    private async Task<JsonElement> SendJsonRpcAsync(
        string method,
        bool authenticated,
        CancellationToken cancellationToken,
        object? parameters = null)
    {
        var id = Interlocked.Increment(ref _tag).ToString();
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["id"] = id
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (authenticated)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"mikun2n:{_password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await _httpClient!.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"n3n 管理接口返回 {(int)response.StatusCode}：{responseBody.Trim()}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var value)
                ? value.GetString()
                : error.ToString();
            throw new InvalidOperationException($"n3n 管理接口错误：{message}");
        }
        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private async Task<IReadOnlyList<JsonElement>> CallLegacyAsync(
        char messageType,
        string command,
        CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        var tag = (Interlocked.Increment(ref _tag) % 1000).ToString();
        var request = $"{messageType} {tag}:1:{_password} {command}";
        var bytes = Encoding.UTF8.GetBytes(request);
        await socket.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, _port), cancellationToken);

        var rows = new List<JsonElement>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        while (true)
        {
            var response = await socket.ReceiveAsync(timeout.Token);
            using var document = JsonDocument.Parse(response.Buffer);
            var root = document.RootElement;

            if (!root.TryGetProperty("_tag", out var responseTag) || responseTag.GetString() != tag)
            {
                continue;
            }

            var type = root.GetProperty("_type").GetString();
            if (type == "error")
            {
                var error = root.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString()
                    : "unknown";
                throw new InvalidOperationException($"n2n 管理接口错误：{error}");
            }

            if (type == "end")
            {
                return rows;
            }

            if (type == "row")
            {
                rows.Add(root.Clone());
            }
        }
    }
}
