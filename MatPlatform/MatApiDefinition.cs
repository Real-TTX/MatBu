// MatPlatform 1.2.0 – a copy from MatDesign, do not edit here
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MatPlatform;

// The types of the platform contract (MatDesign docs/PLATFORM.md, contract version 1). The same types are served by an app and read by a consumer.

/// <summary>The answer of <c>GET /api</c>: what the app is and how a consumer draws it. The values are never in it.</summary>
public sealed record MatApiDefinition
{
    public const int ContractVersion = 1;

    public int Contract { get; init; } = ContractVersion;

    public required MatAppInfo App { get; init; }

    public required MatAuthInfo Auth { get; init; }

    public IReadOnlyList<MatPage> Pages { get; init; } = [];

    public IReadOnlyList<MatStateItem> State { get; init; } = [];

    public IReadOnlyList<MatField> Configure { get; init; } = [];

    public IReadOnlyList<MatAction> Actions { get; init; } = [];

    public IReadOnlyList<MatCapability> Provides { get; init; } = [];

    public IReadOnlyList<MatCapability> Consumes { get; init; } = [];

    /// <summary>The roles of the app, the most powerful first; MatIdentity offers them when an administrator gives the app to people (null: the app has none to offer).</summary>
    public IReadOnlyList<MatRole>? Roles { get; init; }

    /// <summary>The paths the app implements (for the list in <c>GET /api</c>).</summary>
    public IReadOnlyList<string> Endpoints { get; init; } = [];

    /// <summary>
    /// How the contract is written: camelCase, nulls left out (except <see cref="MatStateValue.Value"/>), enums as their names in lower case,
    /// letters as they are (<c>ü</c>, not <c>ü</c>; HTML-sensitive characters stay escaped).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

public sealed record MatAppInfo(string Id, string Name, string Description, string Version, string? Icon, string? Accent, string Home = "/");

public sealed record MatAuthInfo(bool ApiKey, string? Identity);

public sealed record MatPage(string Id, string Label, string Icon, IReadOnlyList<MatSection> Sections);

/// <summary>A part of a page: <c>tiles</c>, <c>form</c>, <c>actions</c> or <c>link</c>.</summary>
public sealed record MatSection(string Kind, IReadOnlyList<string> Items, string? Label = null, string? Url = null)
{
    /// <summary>A grid of tiles: keys of <see cref="MatApiDefinition.State"/>.</summary>
    public static MatSection Tiles(params string[] keys) => new("tiles", keys);

    /// <summary>A form with an optional heading: keys of <see cref="MatApiDefinition.Configure"/>.</summary>
    public static MatSection Form(string? label, params string[] keys) => new("form", keys, label);

    /// <summary>A row of buttons: ids of <see cref="MatApiDefinition.Actions"/>.</summary>
    public static MatSection Actions(params string[] ids) => new("actions", ids);

    /// <summary>A link that opens the app at <paramref name="url"/>.</summary>
    public static MatSection Link(string label, string url) => new("link", [], label, url);
}

/// <summary>A tile: what a value is and how it is shown (<c>number</c>, <c>text</c>, <c>bool</c>, <c>time</c>, <c>meter</c>, <c>status</c>).</summary>
public sealed record MatStateItem(string Key, string Label, string Widget = "number")
{
    public string? Icon { get; init; }
    public string? Unit { get; init; }
    public int? Decimals { get; init; }
    public double? WarnAbove { get; init; }
    public double? ErrorAbove { get; init; }

    /// <summary>For a meter: a number or the key of another value.</summary>
    public JsonElement? Max { get; init; }
}

/// <summary>A setting: <c>text</c>, <c>number</c>, <c>bool</c>, <c>select</c>, <c>url</c>, <c>email</c> or <c>secret</c>.</summary>
public sealed record MatField(string Key, string Label, string Type)
{
    public bool? Required { get; init; }
    public IReadOnlyList<MatOption>? Options { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Pattern { get; init; }
    public string? Placeholder { get; init; }
    public string? Hint { get; init; }
    public string? Group { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? ShowIf { get; init; }

    /// <summary>The value takes effect after a restart.</summary>
    public bool? Restart { get; init; }

    /// <summary>A <see cref="ShowIf"/>: the field is shown only when every named field has the value given here.</summary>
    public static IReadOnlyDictionary<string, JsonElement> When(params (string Key, object? Value)[] conditions)
        => conditions.ToDictionary(c => c.Key, c => JsonSerializer.SerializeToElement(c.Value, MatApiDefinition.Json));
}

public sealed record MatOption(string Value, string Label);

/// <summary>A button: <c>link</c> (opens <see cref="Url"/>) or <c>post</c> (<c>POST /api/actions/{id}</c>).</summary>
public sealed record MatAction(string Id, string Label, string Icon, string Kind)
{
    public string? Url { get; init; }
    public string? Confirm { get; init; }
    public bool? Danger { get; init; }
}

public sealed record MatCapability(string Capability, int Version, bool? Optional = null);

/// <summary>A role of the app: the value of the claim <c>role</c> and its name for people.</summary>
public sealed record MatRole(string Key, string Label);

/// <summary>A value of <c>GET /api/state</c>. The value is always written, also when it is null (a time that has not happened yet).</summary>
public sealed record MatStateValue([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Value, string? Level = null, string? Text = null);

/// <summary>The answer of <c>GET /api/state/{key}</c>: one value with its key. The value is always written, also when it is null.</summary>
public sealed record MatStateEntry(string Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Value, string? Level = null, string? Text = null);

/// <summary>The value of a setting of the type <c>secret</c> as <c>GET /api/configure</c> sends it: whether it is set, never the secret itself.</summary>
public sealed record MatSecret(bool IsSet)
{
    /// <summary>Set when <paramref name="value"/> is not empty.</summary>
    public static MatSecret Of(string? value) => new(!string.IsNullOrEmpty(value));
}

/// <summary>The answer of <c>GET /api/health</c>.</summary>
public sealed record MatHealth(string Status, string App, string Version, DateTimeOffset Time, IReadOnlyList<MatHealthCheck> Checks);

public sealed record MatHealthCheck(string Name, string Status, string? Message = null);

/// <summary>The answer of <c>POST /api/actions/{id}</c>.</summary>
public sealed record MatActionResult(bool Ok, string Message);
