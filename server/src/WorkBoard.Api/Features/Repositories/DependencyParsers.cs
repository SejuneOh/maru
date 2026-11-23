using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkBoard.Api.Features.Repositories;

// Pulls dependency names out of manifest text. Returns null when the text is not a readable manifest.
public static partial class DependencyParsers
{
    public static readonly string[] SkippedFolders =
        ["node_modules", "vendor", "bin", "obj", "dist", "build", "target", "third_party", ".git"];

    // The manager for a file path, or null when the file is not a manifest this reads.
    public static string? ManagerFor(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        if (file == "package.json") return "npm";
        if (file == "go.mod") return "go";
        if (file == "pyproject.toml") return "pip";
        if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return "nuget";
        return null;
    }

    public static IReadOnlyList<string>? Parse(string manager, string text, int maxItems)
    {
        var items = manager switch
        {
            "npm" => ParsePackageJson(text),
            "nuget" => ParseCsproj(text),
            "go" => ParseGoMod(text),
            "pip" => ParsePyproject(text),
            _ => null,
        };
        return items?.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).Take(maxItems).ToList();
    }

    private static List<string>? ParsePackageJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var names = new List<string>();
            foreach (var section in new[] { "dependencies", "devDependencies" })
                if (doc.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                    names.AddRange(deps.EnumerateObject().Select(p => p.Name));
            return names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ParseCsproj(string text) =>
        PackageReference().Matches(text).Select(m => m.Groups[1].Value).ToList();

    private static List<string> ParseGoMod(string text)
    {
        var names = new List<string>();
        var inBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var indirect = line.Contains("// indirect", StringComparison.Ordinal);
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment].Trim();
            if (inBlock)
            {
                if (line == ")") { inBlock = false; continue; }
                if (!indirect && FirstToken(line) is { } m) names.Add(m);
            }
            else if (line == "require (")
                inBlock = true;
            else if (line.StartsWith("require ", StringComparison.Ordinal) && !indirect
                     && FirstToken(line["require ".Length..].Trim()) is { } single)
                names.Add(single);
        }
        return names;
    }

    private static string? FirstToken(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } t ? t[0] : null;

    private static List<string> ParsePyproject(string text)
    {
        var names = new List<string>();
        var section = "";
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && !line.StartsWith("[["))
            {
                var end = line.IndexOf(']');
                section = end > 0 ? line[1..end].Trim() : "";
            }
            else if (section == "project" && Regex.IsMatch(line, @"^dependencies\s*="))
            {
                var array = line[(line.IndexOf('=') + 1)..];
                while (!ArrayClosed(array) && i + 1 < lines.Length)
                    array += "\n" + lines[++i];
                foreach (var quoted in QuotedStrings(array))
                    if (RequirementName().Match(quoted) is { Success: true } r)
                        names.Add(r.Value);
            }
            else if (section == "tool.poetry.dependencies" && KeyName().Match(line) is { Success: true } k
                     && !k.Groups[1].Value.Equals("python", StringComparison.OrdinalIgnoreCase))
                names.Add(k.Groups[1].Value);
        }
        return names;
    }

    // True once the bracket that opens the array is closed, ignoring brackets inside strings ("pkg[extra]").
    private static bool ArrayClosed(string s)
    {
        var depth = 0;
        var quote = '\0';
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return true;
        }
        return false;
    }

    private static IEnumerable<string> QuotedStrings(string s)
    {
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote == '\0') { if (c is '"' or '\'') { quote = c; start = i + 1; } }
            else if (c == quote) { yield return s[start..i]; quote = '\0'; }
        }
    }

    [GeneratedRegex(@"<PackageReference\s+(?:[^>]*?\s)?Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReference();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*")]
    private static partial Regex RequirementName();

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*)\s*=")]
    private static partial Regex KeyName();
}
