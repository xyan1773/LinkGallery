using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LinkGallery.Application.Devices;
using LinkGallery.Domain.Devices;

namespace LinkGallery.Infrastructure.Devices;

public sealed class LocalDeviceDiscovery(HttpClient httpClient) : ILocalDeviceDiscovery
{
    private const int ApiPort = 39570;
    private readonly HttpPublicDeviceInfoClient _publicInfoClient = new(httpClient);

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        string desktopId,
        CancellationToken cancellationToken)
    {
        var udpTask = UdpDiscoveryClient.DiscoverAsync(
            desktopId,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        var adbTask = DiscoverAdbDevicesAsync(cancellationToken);
        await Task.WhenAll(udpTask, adbTask).ConfigureAwait(false);
        var directlyDiscovered = udpTask.Result
            .Concat(adbTask.Result)
            .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (directlyDiscovered.Length > 0)
        {
            return directlyDiscovered;
        }

        return await ProbeLocalSubnetsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscoveredDevice?> ResolvePairingCodeAsync(
        string desktopId,
        string pairingCode,
        CancellationToken cancellationToken)
    {
        var resolved = await UdpDiscoveryClient.ResolvePairingCodeAsync(
            desktopId,
            pairingCode,
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (resolved.Count > 0)
        {
            return resolved[0];
        }
        return null;
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAdbDevicesAsync(
        CancellationToken cancellationToken)
    {
        var adb = FindAdbExecutable();
        if (adb is null) return [];
        var devicesOutput = await RunAdbAsync(adb, ["devices"], cancellationToken).ConfigureAwait(false);
        var serials = devicesOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.EndsWith("\tdevice", StringComparison.Ordinal))
            .Select(line => line[..line.IndexOf('\t')])
            .Where(serial => !string.IsNullOrWhiteSpace(serial))
            .ToArray();
        var devices = new List<DiscoveredDevice>();
        foreach (var serial in serials)
        {
            try
            {
                var portText = await RunAdbAsync(
                    adb,
                    ["-s", serial, "forward", "tcp:0", $"tcp:{ApiPort}"],
                    cancellationToken).ConfigureAwait(false);
                if (!int.TryParse(portText.Trim(), out var localPort)) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var apiAddress = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), localPort, "api/v1/").Uri;
                var info = await _publicInfoClient.GetAsync(apiAddress, timeout.Token).ConfigureAwait(false);
                devices.Add(Map(info, IPAddress.Loopback.ToString(), localPort, DeviceAddressSource.Adb));
            }
            catch (Exception exception) when (
                exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
        return devices;
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> ProbeLocalSubnetsAsync(
        CancellationToken cancellationToken)
    {
        var hosts = LocalIpv4Addresses()
            .SelectMany(HostsInSame24)
            .Distinct()
            .Take(768)
            .ToArray();
        var results = new ConcurrentDictionary<string, DiscoveredDevice>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 48,
            },
            async (host, token) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(450));
                try
                {
                    var apiAddress = new UriBuilder(Uri.UriSchemeHttp, host.ToString(), ApiPort, "api/v1/").Uri;
                    var info = await _publicInfoClient.GetAsync(apiAddress, timeout.Token).ConfigureAwait(false);
                    var device = Map(info, host.ToString(), ApiPort, DeviceAddressSource.Subnet);
                    results.TryAdd(device.DeviceId, device);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
                {
                }
            }).ConfigureAwait(false);
        return results.Values.ToArray();
    }

    internal static IReadOnlyList<IPAddress> LocalIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.Equals(IPAddress.Any))
            .Distinct()
            .ToArray();

    public static IEnumerable<IPAddress> HostsInSame24(IPAddress localAddress)
    {
        var bytes = localAddress.GetAddressBytes();
        if (bytes.Length != 4) yield break;
        for (var last = 1; last < 255; last++)
        {
            if (last == bytes[3]) continue;
            yield return new IPAddress([bytes[0], bytes[1], bytes[2], (byte)last]);
        }
    }

    private static string? FindAdbExecutable()
    {
        var executable = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ANDROID_HOME"),
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.Combine(root!, "platform-tools", executable);
            if (File.Exists(candidate)) return candidate;
        }
        return Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, executable))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunAdbAsync(
        string adb,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(adb)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start adb.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        return process.ExitCode == 0 ? output : string.Empty;
    }

    private static DiscoveredDevice Map(
        PublicDeviceInfo info,
        string host,
        int port,
        DeviceAddressSource source)
    {
        var device = new DiscoveredDevice
        {
            DeviceId = info.DeviceId,
            DisplayName = info.DeviceName,
            Manufacturer = info.Manufacturer,
            Model = info.Model,
            CertificateFingerprint = info.CertificateFingerprint,
            InstanceId = info.InstanceId,
            PairingAvailable = info.PairingAvailable,
        };
        device.Addresses.Add(new DeviceAddress
        {
            DeviceId = info.DeviceId,
            Host = host,
            Port = port,
            Source = source,
        });
        return device;
    }
}
