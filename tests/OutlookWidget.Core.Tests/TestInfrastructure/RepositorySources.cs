using System.Reflection;

namespace OutlookWidget.Core.Tests.TestInfrastructure;

/// <summary>
/// Locates the product's own source and manifest, for the checks that must fail at edit time.
/// </summary>
/// <remarks>
/// Shared by every source-level test rather than duplicated per class. The comment and string
/// stripper in particular has to behave identically everywhere: these files document their own
/// invariants at length, so a rule applied to raw text would fire on the prose explaining it, and
/// two slightly different strippers would mean one test class quietly stopped catching things.
/// </remarks>
internal static class RepositorySources
{
    /// <summary>Reads a path from the assembly metadata the test project sets.</summary>
    private static string Metadata(string key)
    {
        string? configured = typeof(RepositorySources).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)
            ?.Value;

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"The {key} assembly metadata is missing; these checks cannot run.");

        return Path.GetFullPath(configured!);
    }

    private static string Directory(string key)
    {
        string resolved = Metadata(key);
        Assert.True(System.IO.Directory.Exists(resolved), $"Directory not found: {resolved}");
        return resolved;
    }

    private static string File(string key)
    {
        string resolved = Metadata(key);
        Assert.True(System.IO.File.Exists(resolved), $"File not found: {resolved}");
        return resolved;
    }

    public static string CoreSourceDirectory => Directory("CoreSourceDirectory");

    public static string ProviderSourceDirectory => Directory("ProviderSourceDirectory");

    public static string PackageAssetsDirectory => Directory("PackageAssetsDirectory");

    public static string PackageManifestPath => File("PackageManifestPath");

    public static IEnumerable<string> CoreSourceFiles() => SourceFiles(CoreSourceDirectory);

    public static IEnumerable<string> ProviderSourceFiles() => SourceFiles(ProviderSourceDirectory);

    private static IEnumerable<string> SourceFiles(string root) =>
        System.IO.Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase)
                   && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Strips line comments, block comments, and string literals, so a rule never fires on prose
    /// that merely discusses the prohibited construction. These files document their own
    /// invariants at length, so this matters: without it, every check would fail on its own
    /// explanation.
    /// </summary>
    public static string StripCommentsAndStrings(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            // Raw string literal. Must be handled before the ordinary string case, because its
            // body may legitimately contain unescaped quotes — the card template is a raw literal
            // full of JSON, and treating it as an ordinary string would end the literal early and
            // spill JSON into the scanned code.
            if (source[i] == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                int fenceLength = 0;
                while (i + fenceLength < source.Length && source[i + fenceLength] == '"')
                {
                    fenceLength++;
                }

                string fence = new('"', fenceLength);
                int end = source.IndexOf(fence, i + fenceLength, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + fenceLength;
                continue;
            }

            // Block comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }

            // Line comment, which also covers /// documentation
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
                continue;
            }

            // Verbatim string
            if (i + 1 < source.Length && source[i] == '@' && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            // Ordinary string
            if (source[i] == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            output.Append(source[i]);
            i++;
        }

        return output.ToString();
    }
}
