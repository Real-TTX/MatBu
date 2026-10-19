// MatPlatform 1.3.0 – a copy from MatDesign, do not edit here
using Microsoft.AspNetCore.Http;

namespace MatPlatform;

/// <summary>
/// Describes the app for <c>GET /api</c>: who it is, its pages, tiles, settings, actions, roles and capabilities. The description is called once at startup
/// and then for each <c>GET /api</c> with the language of the caller (<see cref="Language"/>, <see cref="T"/>), so it must not depend on anything but the language.
/// <c>auth</c> and <c>endpoints</c> are filled in by the platform.
/// </summary>
public sealed class MatPlatformBuilder
{
    private readonly List<MatPage> _pages = [];
    private readonly List<MatStateItem> _state = [];
    private readonly List<MatField> _configure = [];
    private readonly List<MatAction> _actions = [];
    private readonly List<MatRole> _roles = [];
    private readonly List<MatCapability> _provides = [];
    private readonly List<MatCapability> _consumes = [];
    private MatAppInfo? _app;

    public MatPlatformBuilder(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Language = language;
    }

    /// <summary>The language of the texts: <c>de</c> or <c>en</c> (what the caller asked for, else the app's).</summary>
    public string Language { get; }

    /// <summary>The German or the English text, by <see cref="Language"/>.</summary>
    public string T(string de, string en) => Language == "en" ? en : de;

    /// <summary>Who the app is. <paramref name="id"/> is its id in MatDesign's registry, <paramref name="icon"/> and <paramref name="home"/> are relative to the app.</summary>
    public MatPlatformBuilder App(string id, string name, string description, string version, string? icon = null, string? accent = null, string home = "/")
    {
        _app = new MatAppInfo(id, name, description, version, icon, accent, home);
        return this;
    }

    /// <summary>A page (a menu entry in a consumer) with its sections, drawn top to bottom (<see cref="MatSection.Tiles"/>, <see cref="MatSection.Form"/>, ...).</summary>
    public MatPlatformBuilder Page(string id, string label, string icon, params MatSection[] sections)
    {
        _pages.Add(new MatPage(id, label, icon, sections));
        return this;
    }

    /// <summary>A tile; use the record's properties for unit, decimals, limits and the maximum of a meter.</summary>
    public MatPlatformBuilder Tile(MatStateItem tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        _state.Add(tile);
        return this;
    }

    /// <summary>A tile with a widget and an icon.</summary>
    public MatPlatformBuilder Tile(string key, string label, string widget = "number", string? icon = null, string? unit = null)
        => Tile(new MatStateItem(key, label, widget) { Icon = icon, Unit = unit });

    /// <summary>A setting; use the record's properties for limits, options, hints and <see cref="MatField.ShowIf"/> (<see cref="MatField.When"/>).</summary>
    public MatPlatformBuilder Field(MatField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _configure.Add(field);
        return this;
    }

    /// <summary>A setting of a type, optionally required, with a hint.</summary>
    public MatPlatformBuilder Field(string key, string label, string type = "text", bool required = false, string? hint = null)
        => Field(new MatField(key, label, type) { Required = required ? true : null, Hint = hint });

    /// <summary>A button.</summary>
    public MatPlatformBuilder Action(MatAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Add(action);
        return this;
    }

    /// <summary>A button that opens <paramref name="url"/> (relative to the app).</summary>
    public MatPlatformBuilder LinkAction(string id, string label, string icon, string url)
        => Action(new MatAction(id, label, icon, "link") { Url = url });

    /// <summary>A button that runs <c>POST /api/actions/{id}</c>, maybe after a question and marked as dangerous.</summary>
    public MatPlatformBuilder PostAction(string id, string label, string icon, string? confirm = null, bool danger = false)
        => Action(new MatAction(id, label, icon, "post") { Confirm = confirm, Danger = danger ? true : null });

    /// <summary>A role of the app (the most powerful first): the value of the claim <c>role</c> and its name.</summary>
    public MatPlatformBuilder Role(string key, string label)
    {
        _roles.Add(new MatRole(key, label));
        return this;
    }

    /// <summary>A capability the app offers to the other apps (<c>/api/provides/{capability}</c>).</summary>
    public MatPlatformBuilder Provides(string capability, int version = 1)
    {
        _provides.Add(new MatCapability(capability, version));
        return this;
    }

    /// <summary>A capability of another app this one uses; <paramref name="optional"/>: it works without it.</summary>
    public MatPlatformBuilder Consumes(string capability, int version = 1, bool optional = false)
    {
        _consumes.Add(new MatCapability(capability, version, optional ? true : null));
        return this;
    }

    /// <summary>The definition with the access the platform reports.</summary>
    public MatApiDefinition Build(MatAuthInfo auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        return new MatApiDefinition
        {
            App = _app ?? throw new InvalidOperationException("The description of the platform has no App(...)."),
            Auth = auth,
            Pages = [.. _pages],
            State = [.. _state],
            Configure = [.. _configure],
            Actions = [.. _actions],
            Provides = [.. _provides],
            Consumes = [.. _consumes],
            Roles = _roles.Count == 0 ? null : [.. _roles],
            Endpoints = MatPlatformEndpoints.Paths,
        };
    }

    /// <summary>The definition that <paramref name="describe"/> makes in <paramref name="language"/> (for tests: <see cref="MatPlatformValidator"/>).</summary>
    public static MatApiDefinition Build(Action<MatPlatformBuilder> describe, string language = "de", MatAuthInfo? auth = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        var builder = new MatPlatformBuilder(language);
        describe(builder);
        return builder.Build(auth ?? new MatAuthInfo(false, null));
    }
}

/// <summary>How the platform behaves for this app; set with the second argument of <c>AddMatPlatform</c>.</summary>
public sealed class MatPlatformOptions
{
    /// <summary>The languages the description knows; a caller asking for another one gets <see cref="DefaultLanguage"/>.</summary>
    public static readonly IReadOnlyList<string> Languages = ["de", "en"];

    internal MatPlatformOptions(Action<MatPlatformBuilder> describe) => Describe = describe;

    /// <summary>The description of the app.</summary>
    public Action<MatPlatformBuilder> Describe { get; }

    /// <summary>The language of the app (<c>de</c> or <c>en</c>): texts when the caller asks for none of <see cref="Languages"/>.</summary>
    public string DefaultLanguage { get; set; } = "de";

    /// <summary>
    /// True (default): <c>PUT /api/configure</c> checks every value against the definition (unknown keys, types, <c>required</c>, <c>min</c>, <c>max</c>,
    /// <c>pattern</c>, <c>options</c>, <c>showIf</c>) before the app sees it (<see cref="MatValueChecker"/>). False: the app checks everything itself
    /// (for an app whose settings page has its own rules and messages).
    /// </summary>
    public bool CheckValues { get; set; } = true;

    /// <summary>Requests per minute and caller address on the anonymous <c>/api</c> and <c>/api/health</c>.</summary>
    public int AnonymousPermitPerMinute { get; set; } = 120;

    /// <summary>The language of a request: the first entry of <c>Accept-Language</c> when it is one of <see cref="Languages"/>, else <see cref="DefaultLanguage"/>.</summary>
    public string LanguageOf(HttpContext? http)
    {
        var header = http?.Request.Headers.AcceptLanguage.ToString() ?? string.Empty;
        var first = header.Split(',', 2)[0].Split(';', 2)[0].Trim();
        var primary = first.Split('-', 2)[0].ToLowerInvariant();
        return Languages.Contains(primary) ? primary : DefaultLanguage;
    }

    /// <summary>The definition in <paramref name="language"/>.</summary>
    public MatApiDefinition Definition(string language, MatAuthInfo auth) => MatPlatformBuilder.Build(Describe, language, auth);
}
