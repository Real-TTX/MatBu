using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatBu.Data;
using MatBu.Services;
using MatPlatform;
using Microsoft.Extensions.DependencyInjection;

namespace MatBu.Tests.Platform;

/// <summary>The platform contract of MatDesign docs/PLATFORM.md as MatBu answers it on a fresh installation: shapes, status codes, access.</summary>
public sealed class PlatformApiTests(MatBuFactory site) : IClassFixture<MatBuFactory>
{
    private static readonly string[] Protected = ["/api/state", "/api/state/activeTasks", "/api/configure"];

    [Fact]
    public async Task Health_answers_without_a_key()
    {
        var response = await site.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("matbu", root.GetProperty("app").GetString());
        Assert.Equal(MatBuVersion.Current, root.GetProperty("version").GetString());
        Assert.Contains(root.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "database" && c.GetProperty("status").GetString() == "ok");
    }

    [Fact]
    public async Task The_old_health_endpoints_and_the_machine_endpoints_keep_their_rules()
    {
        var anonymous = site.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);

        // MatBu's own API stays behind its session, the monitoring token and the instance tokens; MAT_API_KEY opens none of it.
        var api = site.Api();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/monitoring/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/secondary/poll", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/gateway/object-test", JsonContent.Create(new { }))).StatusCode);

        var token = site.Services.GetRequiredService<PersistentStore>().GetMonitoringToken();
        using var monitoring = new HttpRequestMessage(HttpMethod.Get, "/api/monitoring/health");
        monitoring.Headers.Add("X-MatBu-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(monitoring)).StatusCode);
    }

    [Fact]
    public async Task The_definition_answers_without_a_key_and_with_an_etag()
    {
        var client = site.CreateClient();
        var response = await client.GetAsync("/api");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var etag = response.Headers.ETag!.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(request)).StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("contract").GetInt32());
        var app = root.GetProperty("app");
        Assert.Equal("matbu", app.GetProperty("id").GetString());
        Assert.Equal("MatBu", app.GetProperty("name").GetString());
        Assert.Equal("#0b7f8a", app.GetProperty("accent").GetString());
        Assert.Equal("/", app.GetProperty("home").GetString());
        Assert.Equal(MatBuVersion.Current, app.GetProperty("version").GetString());
        Assert.True(root.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(root.GetProperty("auth").TryGetProperty("identity", out _));

        Assert.Equal(["overview", "settings"], root.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(["admin", "operator", "user"], root.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("key").GetString()));

        var actions = root.GetProperty("actions").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal("post", actions["runBackups"].GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(actions["runBackups"].GetProperty("confirm").GetString()));
        Assert.Equal("link", actions["open"].GetProperty("kind").GetString());
        Assert.Equal("/", actions["open"].GetProperty("url").GetString());

        Assert.True(root.GetProperty("configure").GetArrayLength() <= 10, "The platform form is for the few settings a fresh install needs.");
        Assert.True(File.Exists(Path.Combine(WebRoot(), app.GetProperty("icon").GetString()!.TrimStart('/'))));
    }

    [Fact]
    public async Task The_definition_follows_accept_language()
    {
        var client = site.CreateClient();
        using var german = new HttpRequestMessage(HttpMethod.Get, "/api");
        german.Headers.TryAddWithoutValidation("Accept-Language", "de-DE");
        using var english = new HttpRequestMessage(HttpMethod.Get, "/api");
        english.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var de = JsonDocument.Parse(await (await client.SendAsync(german)).Content.ReadAsStringAsync());
        using var en = JsonDocument.Parse(await (await client.SendAsync(english)).Content.ReadAsStringAsync());
        Assert.Equal("Übersicht", de.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
        Assert.Equal("Overview", en.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task State_configure_and_actions_need_the_key()
    {
        var anonymous = site.CreateClient();
        foreach (var path in Protected)
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/configure", new { values = new { timeZone = "UTC" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/actions/runBackups", null)).StatusCode);

        var wrong = site.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/state")).StatusCode);

        // A MatBu session is no way in either, and a bearer token is none while MatIdentity is not configured.
        var bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "abc.def.ghi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task State_answers_every_tile_and_one_value_by_key()
    {
        var api = site.Api();
        using var definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        using var state = JsonDocument.Parse(await api.GetStringAsync("/api/state"));
        var values = state.RootElement.GetProperty("values");
        Assert.True(state.RootElement.TryGetProperty("time", out _));

        foreach (var key in definition.RootElement.GetProperty("state").EnumerateArray().Select(s => s.GetProperty("key").GetString()!))
        {
            Assert.True(values.TryGetProperty(key, out var value), $"No value for {key}");
            Assert.True(value.TryGetProperty("value", out _), $"{key} has no value field");
        }

        // A fresh installation: no job, no backup yet.
        Assert.Equal(0, values.GetProperty("activeTasks").GetProperty("value").GetInt32());
        Assert.Equal("info", values.GetProperty("lastResult").GetProperty("value").GetString());
        Assert.False(string.IsNullOrEmpty(values.GetProperty("lastResult").GetProperty("text").GetString()));
        Assert.Equal(JsonValueKind.Null, values.GetProperty("lastBackup").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, values.GetProperty("nextRun").GetProperty("value").ValueKind);
        Assert.Equal(0, values.GetProperty("storageUsed").GetProperty("value").GetInt64());

        var one = await api.GetAsync("/api/state/activeTasks");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using (var doc = JsonDocument.Parse(await one.Content.ReadAsStringAsync()))
        {
            Assert.Equal("activeTasks", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("value").GetInt32());
        }

        var unknown = await api.GetAsync("/api/state/nothing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var doc = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("nothing", out _));
        }
    }

    [Fact]
    public async Task Configure_answers_every_field_and_never_a_secret()
    {
        var api = site.Api();
        using var definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        using var configuration = JsonDocument.Parse(await api.GetStringAsync("/api/configure"));
        var values = configuration.RootElement.GetProperty("values");

        foreach (var field in definition.RootElement.GetProperty("configure").EnumerateArray())
        {
            var key = field.GetProperty("key").GetString()!;
            Assert.True(values.TryGetProperty(key, out var value), $"No value for {key}");
            if (field.GetProperty("type").GetString() == "secret")
            {
                Assert.Equal(JsonValueKind.Object, value.ValueKind);
                Assert.Equal(["isSet"], value.EnumerateObject().Select(p => p.Name));
            }
        }

        Assert.False(values.GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
        Assert.DoesNotContain(MatBuFactory.ApiKey, configuration.RootElement.GetRawText());
        Assert.DoesNotContain(MatBuFactory.ApiKey, definition.RootElement.GetRawText());
    }

    [Fact]
    public async Task Configure_checks_every_value_saves_nothing_when_one_is_wrong_and_keeps_the_secret()
    {
        var api = site.Api();
        var notifications = site.Services.GetRequiredService<NotificationSettingsStore>();

        var bad = await api.PutAsJsonAsync("/api/configure", new { values = new { timeZone = "UTC", smtpPort = 0, nothing = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var errors = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()))
        {
            var byKey = errors.RootElement.GetProperty("errors");
            Assert.True(byKey.TryGetProperty("nothing", out _));
            Assert.True(byKey.TryGetProperty("smtpPort", out _));
        }

        Assert.NotEqual("UTC", site.Services.GetRequiredService<GeneralSettingsStore>().Read().TimeZoneId);

        foreach (var wrong in new object[]
                 {
                     new { timeZone = "Mars/Olympus_Mons" },
                     new { timeZone = "" },
                     new { webhookEnabled = true },
                     new { webhookEnabled = true, webhookUrl = "ftp://hooks.example.com" },
                     new { emailEnabled = true, smtpHost = "mail.example.com" },
                     new { emailEnabled = true, smtpHost = "mail.example.com", smtpPort = 587, mailFrom = "not-an-address", mailRecipients = "ops@example.com" },
                     new { smtpPort = 70000 },
                     new { smtpPort = 1.5 },
                     new { webhookEnabled = "yes" },
                 })
        {
            var response = await api.PutAsJsonAsync("/api/configure", new { values = wrong });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(wrong)}: {(int)response.StatusCode}");
        }

        Assert.False(notifications.Read().WebhookEnabled);
        Assert.False(notifications.Read().EmailEnabled);

        var good = await api.PutAsJsonAsync("/api/configure", new
        {
            values = new
            {
                timeZone = "UTC",
                emailEnabled = true,
                smtpHost = "mail.example.com",
                smtpPort = 465,
                smtpUsername = "backup",
                smtpPassword = "s3cret-password",
                mailFrom = "matbu@example.com",
                mailRecipients = "ops@example.com, it@example.com",
            },
        });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var body = await good.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-password", body);
        using (var saved = JsonDocument.Parse(body))
        {
            var values = saved.RootElement.GetProperty("values");
            Assert.Equal("UTC", values.GetProperty("timeZone").GetString());
            Assert.True(values.GetProperty("emailEnabled").GetBoolean());
            Assert.Equal(465, values.GetProperty("smtpPort").GetInt32());
            Assert.True(values.GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
        }

        Assert.Equal("s3cret-password", notifications.Read().SmtpPassword);
        Assert.Equal("UTC", site.Services.GetRequiredService<GeneralSettingsStore>().Read().TimeZoneId);

        // A change without the secret keeps it; "" clears it.
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync("/api/configure", new { values = new { smtpHost = "mail2.example.com" } })).StatusCode);
        Assert.Equal("s3cret-password", notifications.Read().SmtpPassword);
        Assert.Equal("mail2.example.com", notifications.Read().SmtpHost);

        var cleared = await api.PutAsJsonAsync("/api/configure", new { values = new { smtpPassword = "", emailEnabled = false } });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(string.Empty, notifications.Read().SmtpPassword);
        using (var doc = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("values").GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
        }
    }

    [Fact]
    public async Task Actions_answer_409_without_a_job_and_404_for_an_unknown_one()
    {
        var api = site.Api();
        var run = await api.PostAsync("/api/actions/runBackups", null);
        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
        using (var doc = JsonDocument.Parse(await run.Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/actions/nothing", null)).StatusCode);
        // A link action is no post action.
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/actions/open", null)).StatusCode);
    }

    internal static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatBu.csproj")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "wwwroot");
    }
}

/// <summary>An installation with jobs and runs: the tiles, the health and the action that starts all backups.</summary>
public sealed class PlatformStateTests(SeededFactory site) : IClassFixture<SeededFactory>
{
    [Fact]
    public async Task The_tiles_show_the_jobs_and_runs()
    {
        var api = site.EnsureSeeded().Api();
        using var state = JsonDocument.Parse(await api.GetStringAsync("/api/state"));
        var values = state.RootElement.GetProperty("values");

        Assert.Equal(1, values.GetProperty("activeTasks").GetProperty("value").GetInt32());
        Assert.Equal("error", values.GetProperty("lastResult").GetProperty("value").GetString());
        Assert.Equal("error", values.GetProperty("lastResult").GetProperty("level").GetString());
        Assert.Contains("Nachtsicherung", values.GetProperty("lastResult").GetProperty("text").GetString());
        Assert.Equal(SeededFactory.SucceededAt.ToUnixTimeSeconds(), values.GetProperty("lastBackup").GetProperty("value").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(SeededFactory.NextRun.ToUnixTimeSeconds(), values.GetProperty("nextRun").GetProperty("value").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(1, values.GetProperty("failedJobs24h").GetProperty("value").GetInt32());
        Assert.Equal("error", values.GetProperty("failedJobs24h").GetProperty("level").GetString());
        Assert.Equal(SeededFactory.StoredBytes, values.GetProperty("storageUsed").GetProperty("value").GetInt64());
        Assert.Equal(1, values.GetProperty("objectsAttention").GetProperty("value").GetInt32());
        Assert.Equal(0, values.GetProperty("runningJobs").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Failures_make_the_health_degraded_but_not_down()
    {
        var response = await site.EnsureSeeded().CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("degraded", doc.RootElement.GetProperty("status").GetString());
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("ok", checks["database"]);
        Assert.Equal("degraded", checks["backups"]);
        Assert.Equal("degraded", checks["objects"]);
    }

    [Fact]
    public async Task Run_all_backups_queues_every_active_job_once()
    {
        var api = site.EnsureSeeded().Api();
        var first = await api.PostAsync("/api/actions/runBackups", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        var tasks = site.Services.GetRequiredService<PersistentStore>().Read().Tasks.ToDictionary(t => t.Id);
        Assert.Equal("Geplant", tasks[201].State);
        Assert.Equal("Deaktiviert", tasks[202].State);

        // Already queued: started again it stays queued once.
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/actions/runBackups", null)).StatusCode);
        Assert.Equal("Geplant", site.Services.GetRequiredService<PersistentStore>().Read().Tasks.Single(t => t.Id == 201).State);
    }
}

/// <summary>Without <c>MAT_API_KEY</c> the key is off: the protected endpoints answer 401 to every key.</summary>
public sealed class PlatformWithoutKeyTests(NoKeyFactory site) : IClassFixture<NoKeyFactory>
{
    [Fact]
    public async Task Every_protected_endpoint_is_401_and_the_definition_says_so()
    {
        var client = site.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", MatBuFactory.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/actions/runBackups", null)).StatusCode);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }
}

/// <summary>The definition of MatBu is valid for MatPlatform's validator, in both languages and against the MatUi sprite.</summary>
public sealed class PlatformValidatorTests(MatBuFactory site) : IClassFixture<MatBuFactory>
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
        => Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatBu.Platform.MatBuPlatform.Describe, language), Sprite()));

    [Fact]
    public async Task The_served_definition_is_valid()
        => Assert.Empty(MatPlatformValidator.Validate(await site.CreateClient().GetStringAsync("/api"), Sprite()));

    private static IReadOnlySet<string> Sprite()
        => MatPlatformValidator.ReadSprite(Path.Combine(AppContext.BaseDirectory, "Fixtures", "matui-icons.svg"));
}
