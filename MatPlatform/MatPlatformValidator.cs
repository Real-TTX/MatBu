// MatPlatform 1.4.0 – a copy from MatDesign, do not edit here
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatPlatform;

/// <summary>
/// Checks a MatApiDefinition against the contract (docs/PLATFORM.md): ids and keys unique and camelCase, every key a section names exists, every
/// <c>showIf</c> names another field, the kinds, widgets and types are known, a link has its address, and, with the icon names of the MatUi sprite,
/// every icon is in it. An app calls it in its tests: <c>Assert.Empty(MatPlatformValidator.Validate(definition, MatPlatformValidator.ReadSprite(path)))</c>.
/// </summary>
public static partial class MatPlatformValidator
{
    public static readonly IReadOnlySet<string> SectionKinds = new HashSet<string>(StringComparer.Ordinal) { "tiles", "form", "actions", "link" };

    public static readonly IReadOnlySet<string> Widgets = new HashSet<string>(StringComparer.Ordinal) { "number", "text", "bool", "time", "meter", "status" };

    public static readonly IReadOnlySet<string> FieldTypes = new HashSet<string>(StringComparer.Ordinal) { "text", "number", "bool", "select", "url", "email", "secret" };

    public static readonly IReadOnlySet<string> ActionKinds = new HashSet<string>(StringComparer.Ordinal) { "link", "post" };

    /// <summary>The problems of <paramref name="definition"/>; empty when it is valid. <paramref name="icons"/>: the names of the sprite (null: icons are not checked).</summary>
    public static IReadOnlyList<string> Validate(MatApiDefinition definition, IEnumerable<string>? icons = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var problems = new List<string>();
        var sprite = icons?.ToHashSet(StringComparer.Ordinal);

        if (definition.Contract != MatApiDefinition.ContractVersion)
        {
            problems.Add($"contract is {definition.Contract}, not {MatApiDefinition.ContractVersion}.");
        }

        var app = definition.App;
        if (app is null || !AppIdRegex().IsMatch(app.Id ?? string.Empty))
        {
            problems.Add($"app.id \"{app?.Id}\" is not an id (small letters and digits, a letter first).");
        }

        if (app is not null)
        {
            Required(problems, "app.name", app.Name);
            Required(problems, "app.version", app.Version);
            if (app.Accent is not null && !AccentRegex().IsMatch(app.Accent))
            {
                problems.Add($"app.accent \"{app.Accent}\" is not a colour like #2f6feb.");
            }
        }

        var state = Keys(problems, "state", definition.State.Select(s => s.Key));
        var fields = Keys(problems, "configure", definition.Configure.Select(f => f.Key));
        var actions = Keys(problems, "actions", definition.Actions.Select(a => a.Id));
        Unique(problems, "pages", definition.Pages.Select(p => p.Id));

        foreach (var page in definition.Pages)
        {
            Required(problems, "the label of page " + page.Id, page.Label);
            Icon(problems, sprite, "page " + page.Id, page.Icon);

            foreach (var section in page.Sections)
            {
                if (!SectionKinds.Contains(section.Kind))
                {
                    problems.Add($"page {page.Id}: the section kind \"{section.Kind}\" is not one of {string.Join(", ", SectionKinds)}.");
                    continue;
                }

                if (section.Kind == "link")
                {
                    Required(problems, $"page {page.Id}: the url of a link section", section.Url);
                    continue;
                }

                var (known, what) = section.Kind switch
                {
                    "tiles" => (state, "state"),
                    "form" => (fields, "configure"),
                    _ => (actions, "actions"),
                };
                foreach (var item in section.Items ?? [])
                {
                    if (!known.Contains(item))
                    {
                        problems.Add($"page {page.Id}: the {section.Kind} section names \"{item}\", which is not in {what}.");
                    }
                }
            }
        }

        foreach (var tile in definition.State)
        {
            Required(problems, "the label of state " + tile.Key, tile.Label);
            if (!Widgets.Contains(tile.Widget))
            {
                problems.Add($"state {tile.Key}: the widget \"{tile.Widget}\" is not one of {string.Join(", ", Widgets)}.");
            }

            if (tile.Max is { } max && max.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
            {
                problems.Add($"state {tile.Key}: max is a number or the key of another value.");
            }

            if (tile.Icon is not null)
            {
                Icon(problems, sprite, "state " + tile.Key, tile.Icon);
            }
        }

        foreach (var field in definition.Configure)
        {
            Required(problems, "the label of configure " + field.Key, field.Label);
            if (!FieldTypes.Contains(field.Type))
            {
                problems.Add($"configure {field.Key}: the type \"{field.Type}\" is not one of {string.Join(", ", FieldTypes)}.");
            }

            if (field.Type == "select" && field.Options is not { Count: > 0 })
            {
                problems.Add($"configure {field.Key}: a select has options.");
            }

            if (field.Min is { } min && field.Max is { } max && min > max)
            {
                problems.Add($"configure {field.Key}: min is above max.");
            }

            if (field.Pattern is { } pattern && !IsRegex(pattern))
            {
                problems.Add($"configure {field.Key}: the pattern is not a regular expression.");
            }

            foreach (var named in field.ShowIf?.Keys ?? Enumerable.Empty<string>())
            {
                if (named == field.Key || !fields.Contains(named))
                {
                    problems.Add($"configure {field.Key}: showIf names \"{named}\", which is not another field.");
                }
            }
        }

        foreach (var action in definition.Actions)
        {
            Required(problems, "the label of action " + action.Id, action.Label);
            Icon(problems, sprite, "action " + action.Id, action.Icon);
            if (!ActionKinds.Contains(action.Kind))
            {
                problems.Add($"action {action.Id}: the kind \"{action.Kind}\" is not link or post.");
            }
            else if (action.Kind == "link")
            {
                Required(problems, $"action {action.Id}: the url of a link", action.Url);
            }
        }

        foreach (var role in definition.Roles ?? [])
        {
            if (!RoleRegex().IsMatch(role.Key ?? string.Empty))
            {
                problems.Add($"role \"{role.Key}\": small letters, digits, - and _ only.");
            }

            Required(problems, "the label of role " + role.Key, role.Label);
        }

        Unique(problems, "roles", (definition.Roles ?? []).Select(r => r.Key));

        foreach (var capability in definition.Provides.Concat(definition.Consumes))
        {
            if (!CapabilityRegex().IsMatch(capability.Capability ?? string.Empty) || capability.Version < 1)
            {
                problems.Add($"capability \"{capability.Capability}\" version {capability.Version}: a name like mail.send and a version from 1.");
            }
        }

        return problems;
    }

    /// <summary>The problems of a definition as JSON (the body of <c>GET /api</c>).</summary>
    public static IReadOnlyList<string> Validate(string json, IEnumerable<string>? icons = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        MatApiDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<MatApiDefinition>(json, MatApiDefinition.Json);
        }
        catch (JsonException ex)
        {
            return ["The definition cannot be read: " + ex.Message];
        }

        return definition is null ? ["The definition is empty."] : Validate(definition, icons);
    }

    /// <summary>The icon names of a MatUi sprite file (<c>&lt;symbol id="mat-i-name"&gt;</c>).</summary>
    public static IReadOnlySet<string> ReadSprite(string path)
        => SymbolRegex().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Keys(List<string> problems, string list, IEnumerable<string> keys)
    {
        var all = keys.ToList();
        foreach (var key in all.Where(k => !KeyRegex().IsMatch(k ?? string.Empty)))
        {
            problems.Add($"{list}: \"{key}\" is not camelCase (a small letter first, then letters and digits).");
        }

        Unique(problems, list, all);
        return all.ToHashSet(StringComparer.Ordinal);
    }

    private static void Unique(List<string> problems, string list, IEnumerable<string> keys)
    {
        foreach (var twice in keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{list}: \"{twice.Key}\" is there {twice.Count()} times.");
        }
    }

    private static void Required(List<string> problems, string what, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(what + " is empty.");
        }
    }

    private static void Icon(List<string> problems, HashSet<string>? sprite, string what, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            problems.Add(what + " has no icon.");
        }
        else if (sprite is not null && !sprite.Contains(icon))
        {
            problems.Add($"{what}: the icon \"{icon}\" is not in the sprite.");
        }
    }

    private static bool IsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex AppIdRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex AccentRegex();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex RoleRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$")]
    private static partial Regex CapabilityRegex();

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();
}
