using System.IO.Compression;
using System.Xml.Linq;
using BcNuGetHelper.Models;

namespace BcNuGetHelper.Services;

public static class AppFileParser
{
    private static readonly byte[] NavxMagic = "NAVX"u8.ToArray();
    private const int HeaderSize = 40;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/navx/2015/manifest";

    /// <summary>Parses the NavxManifest.xml from a Business Central .app file (40-byte NAVX header followed by a zip archive).</summary>
    public static AppManifest Parse(byte[] appFile)
    {
        if (appFile.Length <= HeaderSize || !appFile.AsSpan(0, 4).SequenceEqual(NavxMagic))
        {
            throw new InvalidDataException("Not a valid Business Central .app file.");
        }

        using var zipStream = new MemoryStream(appFile, HeaderSize, appFile.Length - HeaderSize, writable: false);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("NavxManifest.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("NavxManifest.xml not found in .app file.");

        using var manifestStream = entry.Open();
        var doc = XDocument.Load(manifestStream);
        var app = doc.Root?.Element(Ns + "App")
            ?? throw new InvalidDataException("App element not found in NavxManifest.xml.");

        var dependencies = doc.Root!
            .Element(Ns + "Dependencies")?
            .Elements(Ns + "Dependency")
            .Select(d => new AppDependency(
                Guid.Parse(Attr(d, "Id")),
                Attr(d, "Name"),
                Attr(d, "Publisher"),
                Attr(d, "MinVersion")))
            .ToList() ?? [];

        return new AppManifest(
            Guid.Parse(Attr(app, "Id")),
            Attr(app, "Name"),
            Attr(app, "Publisher"),
            Attr(app, "Version"),
            app.Attribute("Description")?.Value ?? "",
            dependencies);
    }

    private static string Attr(XElement element, string name) =>
        element.Attribute(name)?.Value
            ?? throw new InvalidDataException($"Attribute '{name}' missing in NavxManifest.xml.");
}
