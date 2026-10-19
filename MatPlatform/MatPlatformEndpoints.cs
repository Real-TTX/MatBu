// MatPlatform 1.3.0 – a copy from MatDesign, do not edit here
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatPlatform;

/// <summary>
/// The platform contract in an app (MatDesign docs/PLATFORM.md): <c>builder.AddMatPlatform(...)</c> registers it, <c>app.MapMatPlatform()</c> maps
/// <c>/api</c>, <c>/api/health</c>, <c>/api/state</c>, <c>/api/state/{key}</c>, <c>/api/configure</c> and <c>/api/actions/{id}</c>.
/// </summary>
public static class MatPlatformEndpoints
{
    /// <summary>The rate limit of the anonymous endpoints <c>/api</c> and <c>/api/health</c>.</summary>
    public const string AnonymousRateLimit = "matplatform-anonymous";

    /// <summary>The paths the platform implements, as <c>GET /api</c> lists them.</summary>
    public static readonly IReadOnlyList<string> Paths = ["/api/health", "/api/state", "/api/configure", "/api/actions"];

    /// <summary>
    /// Registers the platform: the description of the app (<paramref name="describe"/>), the settings from the environment (<c>MAT_API_KEY</c>,
    /// <c>MATIDENTITY_*</c>), the key check and the rate limit of the anonymous endpoints. The values come from the services that implement
    /// <see cref="IMatPlatformState"/>, <see cref="IMatPlatformConfiguration"/>, <see cref="IMatPlatformActions"/> and <see cref="IMatPlatformHealth"/>
    /// (<see cref="AddMatPlatform{TApp}"/> registers one class for all). Returns the settings for <c>AddMatIdentitySignIn</c>.
    /// </summary>
    public static MatPlatformSettings AddMatPlatform(this IHostApplicationBuilder builder, Action<MatPlatformBuilder> describe, Action<MatPlatformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(describe);

        var options = new MatPlatformOptions(describe);
        configure?.Invoke(options);

        // Once at startup: a description that cannot be built fails here, not at the first request; the id names the app in the settings.
        var definition = options.Definition(options.DefaultLanguage, new MatAuthInfo(false, null));
        var settings = MatPlatformSettings.From(builder.Configuration, definition.App.Id);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<MatApiAccess>();

        // Per caller address: a consumer that polls every few seconds is fine, a flood is not.
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(AnonymousRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AnonymousPermitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return settings;
    }

    /// <summary><see cref="AddMatPlatform"/> with <typeparamref name="TApp"/> as the source of the values (see <see cref="AddMatPlatformValues{TApp}"/>).</summary>
    public static MatPlatformSettings AddMatPlatform<TApp>(this IHostApplicationBuilder builder, Action<MatPlatformBuilder> describe, Action<MatPlatformOptions>? configure = null)
        where TApp : class
    {
        var settings = builder.AddMatPlatform(describe, configure);
        builder.Services.AddMatPlatformValues<TApp>();
        return settings;
    }

    /// <summary>Registers <typeparamref name="TApp"/> (scoped) for each of the value interfaces it implements; it must implement at least one.</summary>
    public static IServiceCollection AddMatPlatformValues<TApp>(this IServiceCollection services)
        where TApp : class
    {
        ArgumentNullException.ThrowIfNull(services);

        var found = false;
        services.AddScoped<TApp>();
        found |= Register<IMatPlatformState>();
        found |= Register<IMatPlatformConfiguration>();
        found |= Register<IMatPlatformActions>();
        found |= Register<IMatPlatformHealth>();

        return found
            ? services
            : throw new InvalidOperationException($"{typeof(TApp).Name} implements none of IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth.");

        bool Register<TInterface>()
            where TInterface : class
        {
            if (!typeof(TInterface).IsAssignableFrom(typeof(TApp)))
            {
                return false;
            }

            services.AddScoped(sp => (TInterface)(object)sp.GetRequiredService<TApp>());
            return true;
        }
    }

    /// <summary>
    /// Maps the endpoints. <c>/api</c> and <c>/api/health</c> are anonymous (rate limited: call <c>app.UseRateLimiter()</c>); the rest needs the key or a token.
    /// Returns the protected group <c>/api</c>, for the app's own <c>/api/provides/...</c>.
    /// </summary>
    public static RouteGroupBuilder MapMatPlatform(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var json = MatApiDefinition.Json;

        app.MapGet("/api", (HttpContext http, MatPlatformOptions options, MatPlatformSettings settings, MatApiAccess access) =>
        {
            var definition = options.Definition(options.LanguageOf(http), new MatAuthInfo(access.KeyEnabled, settings.Authority));
            var body = JsonSerializer.SerializeToUtf8Bytes(definition, json);
            // The definition only changes with the version of the app: a consumer asks again cheaply.
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(body))[..16] + "\"";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.ETag = etag;
            return http.Request.Headers.IfNoneMatch.ToString() == etag
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(body, "application/json");
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimit);

        app.MapGet("/api/health", async (HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
        {
            var call = Call(http, options, "anonymous");
            var definition = options.Definition(options.DefaultLanguage, new MatAuthInfo(false, null));
            IReadOnlyList<MatHealthCheck> checks = [];
            if (http.RequestServices.GetService<IMatPlatformHealth>() is { } health)
            {
                try
                {
                    checks = await health.CheckHealthAsync(call, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger(http).LogError(ex, "The health check of the platform failed");
                    checks = [new MatHealthCheck("app", "down", call.T("Die Prüfung ist fehlgeschlagen.", "The check failed."))];
                }
            }

            var status = checks.Any(c => c.Status == "down") ? "down" : checks.Any(c => c.Status == "degraded") ? "degraded" : "ok";
            var answer = new MatHealth(status, definition.App.Id, definition.App.Version, DateTimeOffset.Now, checks);
            return Results.Json(answer, json, statusCode: status == "down" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimit);

        var api = app.MapGroup("/api").AllowAnonymous().AddEndpointFilter(MatApiAccess.RequireAsync).DisableAntiforgery();

        api.MapGet("/state", async (HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
            Results.Json(new { time = DateTimeOffset.Now, values = await StateAsync(http, options, ct) }, json));

        api.MapGet("/state/{key}", async (string key, HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
            (await StateAsync(http, options, ct)).TryGetValue(key, out var value)
                ? Results.Json(new MatStateEntry(key, value.Value, value.Level, value.Text), json)
                : Errors(key, Call(http, options).T("Diesen Wert gibt es nicht.", "There is no such value."), StatusCodes.Status404NotFound));

        api.MapGet("/configure", async (HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
            Results.Json(new { values = await ConfigurationAsync(http, options, ct) }, json));

        api.MapPut("/configure", async (HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
        {
            var call = Call(http, options);
            ConfigureBody? body;
            try
            {
                body = await http.Request.ReadFromJsonAsync<ConfigureBody>(json, ct);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                body = null;
            }

            if (body?.Values is null)
            {
                return Errors(string.Empty, "Send {\"values\": {...}}.", StatusCodes.Status400BadRequest);
            }

            var configuration = http.RequestServices.GetService<IMatPlatformConfiguration>();
            if (options.CheckValues || configuration is null)
            {
                var definition = options.Definition(call.Language, new MatAuthInfo(false, null));
                var current = await ConfigurationAsync(http, options, ct);
                var problems = MatValueChecker.Check(definition, body.Values, current, call.Language);
                if (problems.Count > 0)
                {
                    return Results.Json(new { errors = problems }, json, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            if (configuration is not null && body.Values.Count > 0)
            {
                var errors = await configuration.ConfigureAsync(body.Values, call, ct);
                if (errors.Count > 0)
                {
                    return Results.Json(new { errors }, json, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            return Results.Json(new { values = await ConfigurationAsync(http, options, ct) }, json);
        });

        api.MapPost("/actions/{id}", async (string id, HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
        {
            var call = Call(http, options);
            var definition = options.Definition(call.Language, new MatAuthInfo(false, null));
            var known = definition.Actions.Any(a => a.Id == id && a.Kind == "post");
            var result = known && http.RequestServices.GetService<IMatPlatformActions>() is { } actions
                ? await actions.RunActionAsync(id, call, ct)
                : null;

            return result is null
                ? Errors(id, call.T("Diese Aktion gibt es nicht.", "There is no such action."), StatusCodes.Status404NotFound)
                : Results.Json(result, json, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        return api;
    }

    private static MatPlatformCall Call(HttpContext http, MatPlatformOptions options, string? caller = null)
        => new(http, options.LanguageOf(http), caller ?? MatApiAccess.CallerOf(http));

    private static async Task<IReadOnlyDictionary<string, MatStateValue>> StateAsync(HttpContext http, MatPlatformOptions options, CancellationToken ct)
        => http.RequestServices.GetService<IMatPlatformState>() is { } state
            ? await state.GetStateAsync(Call(http, options), ct)
            : new Dictionary<string, MatStateValue>();

    /// <summary>The values of the settings, with every field of the type <c>secret</c> as <c>{"isSet": bool}</c> whatever the app returned.</summary>
    private static async Task<IReadOnlyDictionary<string, object?>> ConfigurationAsync(HttpContext http, MatPlatformOptions options, CancellationToken ct)
    {
        if (http.RequestServices.GetService<IMatPlatformConfiguration>() is not { } configuration)
        {
            return new Dictionary<string, object?>();
        }

        var values = await configuration.GetConfigurationAsync(Call(http, options), ct);
        var secrets = options.Definition(options.DefaultLanguage, new MatAuthInfo(false, null)).Configure
            .Where(f => f.Type == "secret")
            .Select(f => f.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!values.Keys.Any(secrets.Contains))
        {
            return values;
        }

        return values.ToDictionary(v => v.Key, v => secrets.Contains(v.Key) ? Secret(v.Value) : v.Value);
    }

    /// <summary>A secret as the platform sends it: whether it is set, never the value.</summary>
    internal static MatSecret Secret(object? value) => value switch
    {
        null => new MatSecret(false),
        MatSecret secret => secret,
        bool set => new MatSecret(set),
        string text => MatSecret.Of(text),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => new MatSecret(false),
        JsonElement { ValueKind: JsonValueKind.String } element => MatSecret.Of(element.GetString()),
        JsonElement { ValueKind: JsonValueKind.False } => new MatSecret(false),
        _ => new MatSecret(true),
    };

    private static IResult Errors(string key, string message, int status)
        => Results.Json(new { errors = new Dictionary<string, string> { [key] = message } }, MatApiDefinition.Json, statusCode: status);

    private static ILogger Logger(HttpContext http)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MatPlatformEndpoints));

    private sealed record ConfigureBody(Dictionary<string, JsonElement>? Values);
}
