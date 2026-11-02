using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevCleaner.Core.Rules;

/// <summary>
/// Either a project rule (Markers + Targets: folders next to a marker file) or a path rule (Paths with tokens/wildcards).
/// </summary>
public sealed record CleanupRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Category Category { get; init; } = Category.Custom;
    public RiskLevel Risk { get; init; } = RiskLevel.Review;
    public OsPlatform[] Os { get; init; } = [];
    public string[] Markers { get; init; } = [];
    public string[] Targets { get; init; } = [];
    public string[] Paths { get; init; } = [];
    public bool ContentsOnly { get; init; }
    public string[] Processes { get; init; } = [];
    public string? Note { get; init; }

    [JsonIgnore]
    public bool IsProjectRule => Markers.Length > 0;

    public bool AppliesTo(OsPlatform os) => Os.Length == 0 || Os.Contains(os);
}

public static class RuleLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<CleanupRule> LoadBuiltIn()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DevCleaner.Core.builtin-rules.json")
            ?? throw new InvalidOperationException("Built-in rules resource missing");
        return JsonSerializer.Deserialize<List<CleanupRule>>(s, JsonOptions) ?? [];
    }

    /// <summary>Loads a rule file; invalid rules are dropped and reported in <paramref name="errors"/>.</summary>
    public static IReadOnlyList<CleanupRule> LoadFile(string path, List<string> errors)
    {
        List<CleanupRule>? rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<CleanupRule>>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            return [];
        }

        var valid = new List<CleanupRule>();
        foreach (var r in rules ?? [])
        {
            var problems = Validate(r);
            if (problems.Count == 0)
                valid.Add(r);
            else
                errors.AddRange(problems.Select(p => $"{Path.GetFileName(path)} [{r.Id}]: {p}"));
        }

        return valid;
    }

    public static List<string> Validate(CleanupRule r)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Id))
            errors.Add("id is required");
        if (string.IsNullOrWhiteSpace(r.Name))
            errors.Add("name is required");
        if (r.IsProjectRule == (r.Paths.Length > 0))
            errors.Add("specify either markers+targets or paths");
        if (r.IsProjectRule && r.Targets.Length == 0)
            errors.Add("targets are required with markers");
        foreach (var t in r.Targets)
        {
            if (t.IndexOfAny(['/', '\\', '*', '?']) >= 0 || t is "." or ".." || string.IsNullOrWhiteSpace(t))
                errors.Add($"target '{t}' must be a plain folder name");
        }

        foreach (var p in r.Paths)
        {
            if (p.Contains("..", StringComparison.Ordinal))
                errors.Add($"path '{p}' must not contain '..'");
        }

        return errors;
    }
}
