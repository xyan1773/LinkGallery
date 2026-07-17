namespace LinkGallery.Application.Media;

public static class MediaDevicePresentation
{
    public static string Resolve(
        string mediaDeviceId,
        string? connectedDeviceId,
        string? connectedDeviceName,
        IReadOnlyDictionary<string, string> pairedDeviceNames,
        string unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaDeviceId);
        ArgumentNullException.ThrowIfNull(pairedDeviceNames);
        if (string.Equals(connectedDeviceId, mediaDeviceId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(connectedDeviceName))
        {
            return connectedDeviceName.Trim();
        }

        return pairedDeviceNames.TryGetValue(mediaDeviceId, out var pairedName) &&
            !string.IsNullOrWhiteSpace(pairedName)
                ? pairedName.Trim()
                : unknown;
    }
}
