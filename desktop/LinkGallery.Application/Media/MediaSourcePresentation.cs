using LinkGallery.Domain.Media;

namespace LinkGallery.Application.Media;

public static class MediaSourcePresentation
{
    public static string Describe(
        MediaItem item,
        string unknown,
        string editedExport)
    {
        ArgumentNullException.ThrowIfNull(item);
        var parts = new List<string>(3);
        AddDistinct(parts, item.SourceDevice);
        AddDistinct(parts, item.SourceApplication);
        if (item.IsEditedExport)
        {
            AddDistinct(parts, editedExport);
        }
        return parts.Count == 0 ? unknown : string.Join(" · ", parts);
    }

    private static void AddDistinct(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !parts.Contains(value.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            parts.Add(value.Trim());
        }
    }
}
