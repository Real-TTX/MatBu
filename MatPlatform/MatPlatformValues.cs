// MatPlatform 1.4.0 – a copy from MatDesign, do not edit here
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MatPlatform;

// What an app supplies to the platform contract besides its definition: the values. Each interface is optional; one class may implement all of them
// (register it with AddMatPlatform<TApp> or AddMatPlatformValues<TApp>). The endpoints of MapMatPlatform call them (docs/PLATFORM.md).

/// <summary>One call of the platform API: the request, the language of its texts and who calls.</summary>
/// <param name="Http">The request.</param>
/// <param name="Language">The language the caller asked for with <c>Accept-Language</c> (<c>de</c>, <c>en</c>), else the app's.</param>
/// <param name="Caller">Who calls, for the activity log: <c>api-key</c>, the subject or client of a token; <c>anonymous</c> on <c>/api/health</c>.</param>
public sealed record MatPlatformCall(HttpContext Http, string Language, string Caller)
{
    /// <summary>The German or the English text, by <see cref="Language"/>.</summary>
    public string T(string de, string en) => Language == "en" ? en : de;
}

/// <summary>The values of the tiles (<c>GET /api/state</c>).</summary>
public interface IMatPlatformState
{
    /// <summary>One entry per key of <see cref="MatApiDefinition.State"/> (plus helpers such as the maximum of a meter).</summary>
    Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct);
}

/// <summary>The values of the few settings (<c>GET</c> and <c>PUT /api/configure</c>).</summary>
public interface IMatPlatformConfiguration
{
    /// <summary>
    /// One entry per key of <see cref="MatApiDefinition.Configure"/>. A secret may be returned as <see cref="MatSecret"/>; whatever is returned for a field of the type
    /// <c>secret</c> is turned into <c>{"isSet": bool}</c> before it is sent, so a secret never leaves the app.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct);

    /// <summary>
    /// Checks and saves the values that are in <paramref name="values"/> (only the keys that change; a secret that is not there stays, <c>""</c> clears it)
    /// when all are valid, and writes the change to the app's activity log with <see cref="MatPlatformCall.Caller"/>. Returns the errors per key otherwise
    /// (nothing saved); an empty answer means saved.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct);
}

/// <summary>The actions of the kind <c>post</c> (<c>POST /api/actions/{id}</c>).</summary>
public interface IMatPlatformActions
{
    /// <summary>Runs the action; <c>Ok = false</c> when it cannot run now (409), null when the app does not know it (404).</summary>
    Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct);
}

/// <summary>The checks of <c>GET /api/health</c>. Fast and cheap: no outside call that can hang.</summary>
public interface IMatPlatformHealth
{
    /// <summary>The checks, each <c>ok</c>, <c>degraded</c> or <c>down</c>; the status of the app is the worst of them.</summary>
    Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct);
}
