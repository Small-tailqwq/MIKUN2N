using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace MikuN2N.Services;

public enum PortMappingState
{
    Unavailable,
    CarrierGradeNat,
    Mapped
}

public sealed record PortMappingStatus(PortMappingState State, string Message);

public sealed class UpnpPortMappingService : IDisposable
{
    private const int LeaseSeconds = 600;
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(4);
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _renewalCancellation = new();
    private readonly Uri? _controlUri;
    private readonly string? _serviceType;
    private readonly IPAddress? _localAddress;
    private readonly int _port;
    private Task? _renewalTask;
    private int _disposed;

    private UpnpPortMappingService(
        HttpClient httpClient,
        PortMappingStatus status,
        Uri? controlUri = null,
        string? serviceType = null,
        IPAddress? localAddress = null,
        int port = 0)
    {
        _httpClient = httpClient;
        Status = status;
        _controlUri = controlUri;
        _serviceType = serviceType;
        _localAddress = localAddress;
        _port = port;
    }

    public PortMappingStatus Status { get; }

    public static async Task<UpnpPortMappingService> CreateAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(4)
        };

        try
        {
            var localAddress = FindInternetFacingAddress();
            if (localAddress is null)
            {
                return Unavailable(httpClient, "未找到可访问互联网的 IPv4 网卡，无法检测 UPnP。");
            }

            var descriptionUris = await DiscoverGatewaysAsync(localAddress, cancellationToken);
            if (descriptionUris.Count == 0)
            {
                return Unavailable(httpClient, "未发现路由器 UPnP 服务，当前不会自动开放直连端口。");
            }

            (Uri ControlUri, string ServiceType)? wanService = null;
            foreach (var descriptionUri in descriptionUris)
            {
                try
                {
                    wanService = await ResolveWanServiceAsync(
                        httpClient,
                        descriptionUri,
                        cancellationToken);
                    break;
                }
                catch (InvalidOperationException)
                {
                    // SSDP also returns media renderers and Windows discovery
                    // devices. Continue until an Internet Gateway Device is found.
                }
                catch (HttpRequestException)
                {
                }
            }
            if (wanService is null)
            {
                return Unavailable(httpClient, "路由器未提供 WAN 端口映射服务。");
            }
            var (controlUri, serviceType) = wanService.Value;
            var externalAddressText = await GetExternalAddressAsync(
                httpClient,
                controlUri,
                serviceType,
                cancellationToken);

            if (!IPAddress.TryParse(externalAddressText, out var externalAddress) ||
                !IsGloballyRoutable(externalAddress))
            {
                return new UpnpPortMappingService(
                    httpClient,
                    new PortMappingStatus(
                        PortMappingState.CarrierGradeNat,
                        "检测到运营商级 NAT：路由器 UPnP 正常，但 WAN 不是公网 IPv4，P2P 直连可能受限。"));
            }

            await AddMappingAsync(
                httpClient,
                controlUri,
                serviceType,
                localAddress,
                port,
                cancellationToken);

            var service = new UpnpPortMappingService(
                httpClient,
                new PortMappingStatus(
                    PortMappingState.Mapped,
                    $"已通过 UPnP 开放 UDP {port}，正在优先尝试 P2P 直连。"),
                controlUri,
                serviceType,
                localAddress,
                port);
            service._renewalTask = service.RenewMappingAsync();
            return service;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            httpClient.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            return Unavailable(
                httpClient,
                $"UPnP 自动映射不可用：{GetUsefulMessage(exception)}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _renewalCancellation.Cancel();
        _renewalCancellation.Dispose();
        _httpClient.Dispose();
    }

    private static UpnpPortMappingService Unavailable(HttpClient httpClient, string message) =>
        new(httpClient, new PortMappingStatus(PortMappingState.Unavailable, message));

    private async Task RenewMappingAsync()
    {
        try
        {
            while (!_renewalCancellation.IsCancellationRequested)
            {
                await Task.Delay(RenewalInterval, _renewalCancellation.Token);
                await AddMappingAsync(
                    _httpClient,
                    _controlUri!,
                    _serviceType!,
                    _localAddress!,
                    _port,
                    _renewalCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_renewalCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // The lease remains valid for several minutes. A later reconnect will
            // rediscover the gateway and make the failure visible in the UI/log.
        }
    }

    private static IPAddress? FindInternetFacingAddress()
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(item => item.Address)
                .FirstOrDefault(address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address));
        }
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverGatewaysAsync(
        IPAddress localAddress,
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(localAddress, 0));
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: ssdp:all\r\n\r\n");
        await udp.SendAsync(
            request,
            new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900),
            cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var locations = new HashSet<Uri>();
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                var headers = Encoding.UTF8.GetString(response.Buffer)
                    .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                var location = headers.FirstOrDefault(line =>
                    line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
                if (location is not null &&
                    Uri.TryCreate(location[(location.IndexOf(':') + 1)..].Trim(), UriKind.Absolute, out var uri))
                {
                    locations.Add(uri);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                break;
            }
        }
        return locations.ToArray();
    }

    private static async Task<(Uri ControlUri, string ServiceType)> ResolveWanServiceAsync(
        HttpClient httpClient,
        Uri descriptionUri,
        CancellationToken cancellationToken)
    {
        var xml = await httpClient.GetStringAsync(descriptionUri, cancellationToken);
        var document = XDocument.Parse(xml);
        var service = document.Descendants()
            .Where(element => element.Name.LocalName == "service")
            .Select(element => new
            {
                Type = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName == "serviceType")?.Value,
                ControlUrl = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName == "controlURL")?.Value
            })
            .FirstOrDefault(item =>
                item.Type?.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) == true ||
                item.Type?.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase) == true);

        if (service?.Type is null || string.IsNullOrWhiteSpace(service.ControlUrl))
        {
            throw new InvalidOperationException("路由器未提供 WAN 端口映射服务");
        }
        return (new Uri(descriptionUri, service.ControlUrl), service.Type);
    }

    private static async Task<string> GetExternalAddressAsync(
        HttpClient httpClient,
        Uri controlUri,
        string serviceType,
        CancellationToken cancellationToken)
    {
        var document = await SendSoapAsync(
            httpClient,
            controlUri,
            serviceType,
            "GetExternalIPAddress",
            [],
            cancellationToken);
        return document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "NewExternalIPAddress")
            ?.Value
            .Trim() ?? string.Empty;
    }

    private static Task<XDocument> AddMappingAsync(
        HttpClient httpClient,
        Uri controlUri,
        string serviceType,
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken) =>
        SendSoapAsync(
            httpClient,
            controlUri,
            serviceType,
            "AddPortMapping",
            [
                ("NewRemoteHost", ""),
                ("NewExternalPort", port.ToString()),
                ("NewProtocol", "UDP"),
                ("NewInternalPort", port.ToString()),
                ("NewInternalClient", localAddress.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", "MikuN2N"),
                ("NewLeaseDuration", LeaseSeconds.ToString())
            ],
            cancellationToken);

    private static async Task<XDocument> SendSoapAsync(
        HttpClient httpClient,
        Uri controlUri,
        string serviceType,
        string action,
        IReadOnlyList<(string Name, string Value)> arguments,
        CancellationToken cancellationToken)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = serviceType;
        var body = new XElement(
            soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", soap),
            new XAttribute(
                soap + "encodingStyle",
                "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(
                soap + "Body",
                new XElement(
                    service + action,
                    arguments.Select(argument =>
                        new XElement(argument.Name, argument.Value)))));
        using var request = new HttpRequestMessage(HttpMethod.Post, controlUri)
        {
            Content = new StringContent(
                body.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8,
                "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var fault = TryGetSoapFault(responseBody);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(fault)
                    ? $"路由器返回 HTTP {(int)response.StatusCode}"
                    : fault);
        }
        return string.IsNullOrWhiteSpace(responseBody)
            ? new XDocument()
            : XDocument.Parse(responseBody);
    }

    private static string? TryGetSoapFault(string responseBody)
    {
        try
        {
            var document = XDocument.Parse(responseBody);
            return document.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName is "errorDescription" or "faultstring")
                ?.Value
                .Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGloballyRoutable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] != 10 &&
               !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
               !(bytes[0] == 127) &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
               !(bytes[0] == 192 && bytes[1] == 168) &&
               !(bytes[0] == 0) &&
               bytes[0] < 224;
    }

    private static string GetUsefulMessage(Exception exception) =>
        exception is HttpRequestException { InnerException: { } inner }
            ? inner.Message
            : exception.Message;
}
