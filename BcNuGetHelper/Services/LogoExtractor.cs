using System.IO.Compression;
using System.Xml.Linq;

namespace BcNuGetHelper.Services;

/// <summary>Extracts the logo image embedded in a Business Central .app package.</summary>
public static class LogoExtractor
{
    public record Logo(byte[] Content, string ContentType);

    public static Logo? TryExtract(byte[] appFile)
    {
        // A .app file prefixes the zip payload with a header; the zip starts at the first local file header signature.
        var zipStart = FindZipStart(appFile);
        if (zipStart < 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(appFile, zipStart, appFile.Length - zipStart, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            var manifest = archive.Entries.FirstOrDefault(
                e => e.Name.Equals("NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
            if (manifest is null)
            {
                return null;
            }

            string? logoPath;
            using (var stream = manifest.Open())
            {
                logoPath = ReadLogoPath(stream);
            }
            if (string.IsNullOrWhiteSpace(logoPath))
            {
                return null;
            }

            var entry = FindEntry(archive, logoPath);
            if (entry is null)
            {
                return null;
            }

            using var entryStream = entry.Open();
            using var output = new MemoryStream();
            entryStream.CopyTo(output);
            return new Logo(output.ToArray(), ContentTypeFor(entry.Name));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int FindZipStart(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
            {
                return i;
            }
        }
        return -1;
    }

    private static string? ReadLogoPath(Stream manifestStream)
    {
        var doc = XDocument.Load(manifestStream);
        // The logo path is a "Logo" attribute on the App element (or, in some manifests, a Logo element).
        var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "App");
        var logo = app?.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals("Logo", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(logo))
        {
            logo = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("Logo", StringComparison.OrdinalIgnoreCase))?.Value;
        }
        return logo;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string logoPath)
    {
        var normalized = logoPath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);
        return archive.GetEntry(normalized)
            ?? archive.Entries.FirstOrDefault(
                e => e.FullName.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(
                e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
}
