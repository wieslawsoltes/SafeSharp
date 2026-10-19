using System.Runtime.InteropServices;

namespace SafeSharp.Tool;

internal static class FrameworkReferences
{
    public static IEnumerable<string> Resolve()
    {
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        var runtime = Environment.Version;
        if (Directory.Exists(packs))
        {
            var referenceDirectory = Directory.EnumerateDirectories(packs)
                .Select(directory => new { Directory = directory, Version = ParseVersion(Path.GetFileName(directory)) })
                .Where(pack => pack.Version is not null && pack.Version.Major == runtime.Major && pack.Version.Minor == runtime.Minor)
                .OrderByDescending(pack => pack.Version)
                .Select(pack => Path.Combine(pack.Directory, "ref", $"net{runtime.Major}.{runtime.Minor}"))
                .FirstOrDefault(Directory.Exists);
            if (referenceDirectory is not null)
                return Directory.EnumerateFiles(referenceDirectory, "*.dll").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        var platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(platformAssemblies))
            throw new ToolException("The runtime did not supply framework metadata references for standalone-source analysis.");
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var framework = platformAssemblies.Split(Path.PathSeparator)
            .Where(path => string.Equals(Path.GetDirectoryName(path), runtimeDirectory, comparer))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (framework.Length == 0)
            throw new ToolException("No .NET framework metadata could be found. Install a matching .NET SDK reference pack.");
        return framework;
    }

    private static Version? ParseVersion(string value) => Version.TryParse(value, out var version) ? version : null;
}
