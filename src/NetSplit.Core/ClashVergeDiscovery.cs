using YamlDotNet.RepresentationModel;

namespace NetSplit.Core;

public static class ClashVergeDiscovery
{
    private const string AppDirectoryName = "io.github.clash-verge-rev.clash-verge-rev";

    public static string? FindCurrentProfilePath()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDirectoryName);
        var indexPath = Path.Combine(baseDirectory, "profiles.yaml");
        if (!File.Exists(indexPath))
        {
            return null;
        }

        var stream = new YamlStream();
        using var reader = File.OpenText(indexPath);
        stream.Load(reader);
        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("current"), out var currentNode)
            || currentNode is not YamlScalarNode current
            || string.IsNullOrWhiteSpace(current.Value))
        {
            return null;
        }

        var path = Path.Combine(baseDirectory, "profiles", $"{current.Value}.yaml");
        return File.Exists(path) ? path : null;
    }

    public static string? FindGeoDataDirectory(IEnumerable<string>? candidateDirectories = null)
    {
        candidateDirectories ??=
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Clash Verge",
                "resources"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppDirectoryName)
        ];

        return candidateDirectories.FirstOrDefault(directory =>
            !string.IsNullOrWhiteSpace(directory)
            && File.Exists(Path.Combine(directory, "geoip.dat"))
            && File.Exists(Path.Combine(directory, "geosite.dat")));
    }
}
