using System.Net;
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
                    "discover",
                    nonce,
                    desktopId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(payload);
            await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken)
                .ConfigureAwait(false);
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
