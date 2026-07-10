using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkGallery.Domain.Devices;

namespace LinkGallery.Infrastructure.Devices;

public sealed class UdpDiscoveryClient
{
    public const string Magic = "LINKGALLERY_DISCOVERY_V1";
    public const int DiscoveryPort = 39571;
    private static readonly TimeSpan[] SendSchedule =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(1000),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        string desktopId,
        TimeSpan listenWindow,
        CancellationToken cancellationToken) =>
        await QueryAsync(
            desktopId,
            "discover",
            pairingCode: null,
            listenWindow,
            cancellationToken).ConfigureAwait(false);

    public static async Task<IReadOnlyList<DiscoveredDevice>> ResolvePairingCodeAsync(
        string desktopId,
        string pairingCode,
        TimeSpan listenWindow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopId);
        if (pairingCode.Length != 6 || pairingCode.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("Pairing code must contain exactly six digits.", nameof(pairingCode));
        }

        return await QueryAsync(
            desktopId,
            "resolve_pairing_code",
            pairingCode,
            listenWindow,
            cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<IPAddress> GetBroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var index = 0; index < broadcast.Length; index++)
                {
                    broadcast[index] = (byte)(address[index] | ~mask[index]);
                }
                targets.Add(new IPAddress(broadcast));
            }
        }
        return targets.ToArray();
    }

    private static async Task<IReadOnlyList<DiscoveredDevice>> QueryAsync(
        string desktopId,
        string type,
        string? pairingCode,
        TimeSpan listenWindow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopId);
        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
        };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var nonce = Guid.NewGuid().ToString("N");
        var receiveTask = ReceiveAnnouncementsAsync(udp, nonce, listenWindow, cancellationToken);
        foreach (var delay in SendSchedule)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var payload = JsonSerializer.Serialize(
                new DiscoverDto(
                    Magic,
                    type,
                    nonce,
                    desktopId,
                    pairingCode,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(payload);
            foreach (var target in GetBroadcastTargets())
            {
                try
                {
                    await udp.SendAsync(bytes, new IPEndPoint(target, DiscoveryPort), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // A disconnected VPN/USB adapter can reject its directed broadcast.
                    // Other active interfaces must still be queried.
                }
            }
        }

        return await receiveTask.ConfigureAwait(false);
    }

    public static DiscoveredDevice? ParseAnnouncement(string payload, IPAddress remoteAddress, string expectedNonce)
    {
        var dto = JsonSerializer.Deserialize<AnnounceDto>(payload, JsonOptions);
        if (dto is null ||
            dto.Magic != Magic ||
            dto.Type != "announce" ||
            dto.Nonce != expectedNonce ||
            string.IsNullOrWhiteSpace(dto.DeviceId))
        {
            return null;
        }

        var host = string.IsNullOrWhiteSpace(dto.Host) ? remoteAddress.ToString() : dto.Host;
        var device = new DiscoveredDevice
        {
            DeviceId = dto.DeviceId,
            DisplayName = string.IsNullOrWhiteSpace(dto.Name) ? dto.DeviceId : dto.Name,
            Model = dto.Model,
            CertificateFingerprint = dto.CertificateFingerprint,
            InstanceId = dto.InstanceId,
            PairingAvailable = dto.PairingAvailable,
        };
        device.Addresses.Add(
            new DeviceAddress
            {
                DeviceId = dto.DeviceId,
                Host = host,
                Port = dto.Port,
                Source = DeviceAddressSource.Udp,
            });
        return device;
    }

    private static async Task<IReadOnlyList<DiscoveredDevice>> ReceiveAnnouncementsAsync(
        UdpClient udp,
        string nonce,
        TimeSpan listenWindow,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(listenWindow);
        var devices = new List<DiscoveredDevice>();
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var payload = Encoding.UTF8.GetString(result.Buffer);
                var device = ParseAnnouncement(payload, result.RemoteEndPoint.Address, nonce);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (JsonException)
            {
            }
        }

        return devices;
    }

    private sealed record DiscoverDto(
        string Magic,
        string Type,
        string Nonce,
        string DesktopId,
        string? PairingCode,
        long Timestamp);

    private sealed record AnnounceDto(
        string Magic,
        string Type,
        string Nonce,
        string DeviceId,
        string? Name,
        string? Model,
        string? Host,
        int Port,
        int ApiVersion,
        string? InstanceId,
        bool PairingAvailable,
        string? CertificateFingerprint,
        long Timestamp,
        string? Signature);
}
