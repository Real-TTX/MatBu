using System.Net;
using MatBu.Data;
using MatBu.Models;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MatBu.Tests.Platform;

/// <summary>
/// The whole app in memory on its own empty data folder, in Development, without its background services (scheduler, workers, secondary
/// connection). <see cref="ApiKey"/> is <c>MAT_API_KEY</c> unless <c>withKey</c> is false; with an <c>authority</c> the sign-in through
/// MatIdentity is on, with a fixed OpenID configuration so no MatIdentity has to run (the tests look at the redirects only).
/// Program and the stores read their settings from environment variables, so the test classes run one after another (Usings.cs).
/// </summary>
public class MatBuFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key-0123456789";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matbu-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _authority;
    private readonly bool _reachable;

    public MatBuFactory()
        : this(withKey: true, authority: null)
    {
    }

    protected MatBuFactory(bool withKey, string? authority, bool reachable = true)
    {
        _authority = authority;
        _reachable = reachable;
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable("MATBU_DATA_PATH", _data);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ApiKeyVariable, withKey ? ApiKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.AuthorityVariable, authority);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientIdVariable, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientSecretVariable, authority is null ? null : "test-secret");
        Environment.SetEnvironmentVariable(MatPlatformSettings.InternalUrlVariable, null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Program reads these before the host is built; set them again in case another factory changed them in between.
        Environment.SetEnvironmentVariable("MATBU_DATA_PATH", _data);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            if (_authority is not null && _reachable)
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = _authority,
                    AuthorizationEndpoint = _authority + "/connect/authorize",
                    TokenEndpoint = _authority + "/connect/token",
                    EndSessionEndpoint = _authority + "/connect/logout",
                };

                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            }
        });
    }

    /// <summary>A client that keeps cookies and does not follow redirects (the tests look at each step).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A browser that already has <paramref name="cookies"/> (for example a session created through a service).</summary>
    public HttpClient Browser(CookieContainer cookies)
    {
        _ = Server;
        return new HttpClient(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() }) { BaseAddress = new Uri("http://localhost") };
    }

    public HttpClient Api()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>Changes the data of the app through its store.</summary>
    public void Update(Action<AppData> change) => Services.GetRequiredService<PersistentStore>().Update(change);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // The other tests build their stores without these.
        foreach (var variable in new[] { "MATBU_DATA_PATH", MatPlatformSettings.ApiKeyVariable, MatPlatformSettings.AuthorityVariable, MatPlatformSettings.ClientSecretVariable })
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the temp folder is cleaned by the system later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Without <c>MAT_API_KEY</c>: the protected endpoints answer 401 to everyone.</summary>
public sealed class NoKeyFactory() : MatBuFactory(withKey: false, authority: null);

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not need to run).</summary>
public sealed class IdentityFactory() : MatBuFactory(withKey: true, authority: IdentityFactory.Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> pointing at an address where nothing answers (MatIdentity is down).</summary>
public sealed class UnreachableIdentityFactory() : MatBuFactory(withKey: true, authority: "http://127.0.0.1:9", reachable: false);

/// <summary>An installation with one backup job, a successful run two hours ago and a failed run one hour ago.</summary>
public sealed class SeededFactory : MatBuFactory
{
    public const long StoredBytes = 123_456_789;

    public static readonly DateTimeOffset SucceededAt = DateTimeOffset.UtcNow.AddHours(-2);

    public static readonly DateTimeOffset NextRun = DateTimeOffset.UtcNow.AddHours(5);

    private readonly object _gate = new();
    private bool _seeded;

    /// <summary>Seeds once, before the first request of a test.</summary>
    public SeededFactory EnsureSeeded()
    {
        lock (_gate)
        {
            if (_seeded) return this;
            Update(data =>
            {
                var now = DateTimeOffset.UtcNow;
                data.Objects.Add(new BackupObject { Id = 101, Name = "Daten", Kind = ObjectKind.LocalFolder, Direction = ObjectDirection.Source, Location = "/source", Status = ObjectStatus.Healthy, InstanceId = 1, CreateDate = now, UpdateDate = now });
                data.Objects.Add(new BackupObject { Id = 102, Name = "Archiv", Kind = ObjectKind.LocalFolder, Direction = ObjectDirection.Target, Location = "/archive", Status = ObjectStatus.Warning, InstanceId = 1, CreateDate = now, UpdateDate = now });
                data.Tasks.Add(new BackupTask { Id = 201, Token = "task-201", Name = "Nachtsicherung", SourceId = 101, TargetId = 102, Enabled = true, State = "Bereit", NextRunDate = NextRun, CreateDate = now, UpdateDate = now });
                data.Tasks.Add(new BackupTask { Id = 202, Token = "task-202", Name = "Alt", SourceId = 101, TargetId = 102, Enabled = false, State = "Deaktiviert", CreateDate = now, UpdateDate = now });
                data.TransferJobs.Add(new TransferJob { Id = 301, TaskId = 201, TaskToken = "task-201", TaskName = "Nachtsicherung", State = "Completed", SourceObjectKind = "LocalFolder", ResolvedDestination = "/archive/1.tar", SourceBytes = StoredBytes, CreateDate = SucceededAt.AddMinutes(-5), UpdateDate = SucceededAt });
                data.TransferJobs.Add(new TransferJob { Id = 302, TaskId = 201, TaskToken = "task-201", TaskName = "Nachtsicherung", State = "Fehler", SourceObjectKind = "LocalFolder", Error = "Ziel nicht erreichbar", CreateDate = now.AddHours(-1).AddMinutes(-5), UpdateDate = now.AddHours(-1) });
            });
            _seeded = true;
            return this;
        }
    }
}
