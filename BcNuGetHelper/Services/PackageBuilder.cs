using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BcNuGetHelper.Models;

namespace BcNuGetHelper.Services;

public static class PackageBuilder
{
    public const string FeedApps = "apps";
    public const string FeedRuntime = "runtime";
    public const string FeedSymbols = "symbols";

    public static readonly string[] Feeds = [FeedApps, FeedRuntime, FeedSymbols];

    private static readonly XNamespace NuspecNs = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";

    /// <summary>Builds a .nupkg containing the nuspec and the .app payload for the given feed.</summary>
    public static byte[] Build(AppManifest manifest, byte[] appFile, string feed)
    {
        var payload = feed switch
        {
            // TODO: transform the .app file to a runtime package during upload
            FeedRuntime => appFile,
            // TODO: strip the .app file down to symbols only during upload
            FeedSymbols => appFile,
            _ => appFile,
        };

        var appFileName =
            $"{AppManifest.Sanitize(manifest.Publisher)}_{AppManifest.Sanitize(manifest.Name)}_{manifest.Version}.app";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = zip.CreateEntry($"{manifest.PackageId}.nuspec");
            using (var stream = nuspecEntry.Open())
            {
                var bytes = Encoding.UTF8.GetBytes(BuildNuspec(manifest));
                stream.Write(bytes);
            }

            var appEntry = zip.CreateEntry(appFileName);
            using (var stream = appEntry.Open())
            {
                stream.Write(payload);
            }
        }
        return ms.ToArray();
    }

    private static string BuildNuspec(AppManifest manifest)
    {
        var metadata = new XElement(NuspecNs + "metadata",
            new XElement(NuspecNs + "id", manifest.PackageId),
            new XElement(NuspecNs + "version", VersionHelper.Normalize(manifest.Version)),
            new XElement(NuspecNs + "authors", manifest.Publisher),
            new XElement(NuspecNs + "description",
                string.IsNullOrEmpty(manifest.Description)
                    ? $"{manifest.Name} by {manifest.Publisher}"
                    : manifest.Description));

        if (manifest.Dependencies.Count > 0)
        {
            metadata.Add(new XElement(NuspecNs + "dependencies",
                manifest.Dependencies.Select(d => new XElement(NuspecNs + "dependency",
                    new XAttribute("id", new AppManifest(d.Id, d.Name, d.Publisher, d.MinVersion, "", []).PackageId),
                    new XAttribute("version", VersionHelper.Normalize(d.MinVersion))))));
        }

        var doc = new XDocument(new XElement(NuspecNs + "package", metadata));
        return doc.Declaration is null
            ? "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + doc
            : doc.ToString();
    }
}
