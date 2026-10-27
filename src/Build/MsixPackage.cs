using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace KNE;

internal static class MsixPackage
{
    internal static async Task CreateAsync(string payload, string output, PlayerManifest game, string version, Action<string>? log, CancellationToken token)
    {
        string assets = Path.Combine(payload, "Assets"); Directory.CreateDirectory(assets);
        WriteLogo(Path.Combine(assets, "Square150x150Logo.png"), 150);
        WriteLogo(Path.Combine(assets, "Square44x44Logo.png"), 44);
        WriteLogo(Path.Combine(assets, "StoreLogo.png"), 50);
        Manifest(game, version).Save(Path.Combine(payload, "AppxManifest.xml"));
        string sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        string? makeAppx = Directory.Exists(sdk) ? Directory.EnumerateDirectories(sdk).OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => Path.Combine(path, "x64", "makeappx.exe")).FirstOrDefault(File.Exists) : null;
        if (makeAppx == null) throw new FileNotFoundException("Для MSIX установите Windows SDK с MakeAppx.exe. Пакет создаётся без цифровой подписи.");
        await GameBuildExporter.RunToolAsync(makeAppx, ["pack", "/d", payload, "/p", output, "/o"], payload, log, token).ConfigureAwait(false);
    }

    internal static XDocument Manifest(PlayerManifest game, string version)
    {
        XNamespace f = "http://schemas.microsoft.com/appx/manifest/foundation/windows10", uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10", rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        return new(new XElement(f + "Package", new XAttribute(XNamespace.Xmlns + "uap", uap), new XAttribute(XNamespace.Xmlns + "rescap", rescap), new XAttribute("IgnorableNamespaces", "uap rescap"),
            new XElement(f + "Identity", new XAttribute("Name", "KNEGame." + game.GameId), new XAttribute("Publisher", "CN=" + game.GameId), new XAttribute("Version", version), new XAttribute("ProcessorArchitecture", "x64")),
            new XElement(f + "Properties", new XElement(f + "DisplayName", game.Title), new XElement(f + "PublisherDisplayName", string.IsNullOrWhiteSpace(game.Team) ? string.IsNullOrWhiteSpace(game.Author) ? game.Title : game.Author : game.Team), new XElement(f + "Logo", "Assets\\StoreLogo.png")),
            new XElement(f + "Resources", new XElement(f + "Resource", new XAttribute("Language", "en-us")), new XElement(f + "Resource", new XAttribute("Language", "ru-ru"))),
            new XElement(f + "Dependencies", new XElement(f + "TargetDeviceFamily", new XAttribute("Name", "Windows.Desktop"), new XAttribute("MinVersion", "10.0.17763.0"), new XAttribute("MaxVersionTested", "10.0.26100.0"))),
            new XElement(f + "Applications", new XElement(f + "Application", new XAttribute("Id", "Game"), new XAttribute("Executable", "Game.exe"), new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
                new XElement(uap + "VisualElements", new XAttribute("DisplayName", game.Title), new XAttribute("Description", game.Title), new XAttribute("BackgroundColor", "transparent"), new XAttribute("Square150x150Logo", "Assets\\Square150x150Logo.png"), new XAttribute("Square44x44Logo", "Assets\\Square44x44Logo.png")))),
            new XElement(f + "Capabilities", new XElement(rescap + "Capability", new XAttribute("Name", "runFullTrust")))));
    }

    private static void WriteLogo(string file, int size)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 128; pixels[i + 1] = 65; pixels[i + 2] = 86; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(file); encoder.Save(stream);
    }
}
