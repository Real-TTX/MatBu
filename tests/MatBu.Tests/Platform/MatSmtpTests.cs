using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Security;
using MatBu.Platform;
using MatBu.Services;
using MatPlatform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MatBu.Tests.Platform;

/// <summary>Which server MatBu's mail goes to, with and without the <c>MATSMTP_*</c> variables (MatDesign docs/PLATFORM.md "Mail through MatSMTP").</summary>
public sealed class MailServerTests
{
    private static readonly string[] Fixed = ["smtpHost", "smtpPort", "smtpUsername", "smtpPassword", "mailFrom"];

    private static readonly NotificationSettings Own = new()
    {
        EmailEnabled = true,
        SmtpHost = "smtp.own.example",
        SmtpPort = 587,
        SmtpUseSsl = true,
        SmtpUsername = "own-user",
        SmtpPassword = "own-password",
        MailFrom = "backup@own.example",
        MailRecipients = "ops@own.example",
        NotifyOnFailure = true,
    };

    private static MailServer Server(params (string Name, string Value)[] variables)
        => new(MatSmtpSettings.From(new ConfigurationBuilder().AddInMemoryCollection(variables.Select(v => KeyValuePair.Create(v.Name, (string?)v.Value))).Build()));

    [Fact]
    public void Without_the_variables_the_own_settings_are_used_as_before()
    {
        var server = Server();
        Assert.False(server.IsFixed);
        Assert.Null(server.Describe());

        var client = server.Compose(Own);
        Assert.Equal(new SmtpClientSettings("smtp.own.example", 587, SmtpSecurity.StartTls, "own-user", "own-password", "backup@own.example", false), client);
        Assert.Equal(SecureSocketOptions.StartTls, client.SocketOptions);

        var plain = server.Compose(new NotificationSettings { SmtpHost = "smtp.own.example", SmtpPort = 25, SmtpUseSsl = false, SmtpPassword = "ignored", MailFrom = "a@b.example" });
        Assert.Equal(SmtpSecurity.None, plain.Security);
        Assert.Equal(SecureSocketOptions.None, plain.SocketOptions);
        Assert.Null(plain.Username);
        Assert.Null(plain.Password);

        var inForce = server.InForce(Own);
        Assert.Equal(JsonSerializer.Serialize(Own), JsonSerializer.Serialize(inForce));
        Assert.Same(Own, server.KeepFixed(Own, new NotificationSettings()));
        Assert.Null(server.Validate(Own));
    }

    [Theory]
    [InlineData("none", SmtpSecurity.None, SecureSocketOptions.None)]
    [InlineData("starttls", SmtpSecurity.StartTls, SecureSocketOptions.StartTls)]
    [InlineData("TLS", SmtpSecurity.Tls, SecureSocketOptions.SslOnConnect)]
    [InlineData("", SmtpSecurity.None, SecureSocketOptions.None)]
    public void With_the_variables_all_mail_goes_through_matsmtp(string tls, SmtpSecurity security, SecureSocketOptions options)
    {
        var server = Server(
            ("MATSMTP_HOST", "mattiverse-matsmtp-matsmtp"),
            ("MATSMTP_PORT", "2025"),
            ("MATSMTP_USERNAME", "matbu"),
            ("MATSMTP_PASSWORD", "relay-password"),
            ("MATSMTP_FROM", "matbu@example.com"),
            ("MATSMTP_TLS", tls));
        Assert.True(server.IsFixed);

        var client = server.Compose(Own);
        Assert.Equal(new SmtpClientSettings("mattiverse-matsmtp-matsmtp", 2025, security, "matbu", "relay-password", "matbu@example.com", true), client);
        Assert.Equal(options, client.SocketOptions);

        var sentence = server.Describe("de")!;
        Assert.Contains("MatSMTP (mattiverse-matsmtp-matsmtp:2025", sentence);
        Assert.Contains("matbu@example.com", sentence);
        Assert.DoesNotContain("relay-password", sentence);
    }

    [Fact]
    public void Without_a_user_matsmtp_is_used_without_authentication_and_without_a_sender_the_own_one()
    {
        var client = Server(("MATSMTP_HOST", "matsmtp")).Compose(Own);
        Assert.Equal(new SmtpClientSettings("matsmtp", MatSmtpSettings.DefaultPort, SmtpSecurity.None, null, null, "backup@own.example", true), client);
    }

    [Fact]
    public void The_fixed_fields_show_matsmtp_and_a_change_of_them_is_never_saved()
    {
        var server = Server(("MATSMTP_HOST", "matsmtp"), ("MATSMTP_USERNAME", "matbu"), ("MATSMTP_PASSWORD", "relay"), ("MATSMTP_FROM", "matbu@example.com"), ("MATSMTP_TLS", "tls"));

        var inForce = server.InForce(Own);
        Assert.Equal(("matsmtp", 2025, true, "matbu", "relay", "matbu@example.com"), (inForce.SmtpHost, inForce.SmtpPort, inForce.SmtpUseSsl, inForce.SmtpUsername, inForce.SmtpPassword, inForce.MailFrom));
        Assert.Equal(Own.MailRecipients, inForce.MailRecipients);
        Assert.Equal("smtp.own.example", Own.SmtpHost);

        var posted = new NotificationSettings
        {
            EmailEnabled = false,
            SmtpHost = "evil.example",
            SmtpPort = 1,
            SmtpUseSsl = false,
            SmtpUsername = "evil",
            SmtpPassword = "evil-password",
            MailFrom = "evil@example.com",
            MailRecipients = "new@own.example",
            NotifyOnSuccess = true,
        };
        var kept = server.KeepFixed(posted, Own);
        Assert.Equal(("smtp.own.example", 587, true, "own-user", "", "backup@own.example"), (kept.SmtpHost, kept.SmtpPort, kept.SmtpUseSsl, kept.SmtpUsername, kept.SmtpPassword, kept.MailFrom));
        Assert.Equal(("new@own.example", false, true), (kept.MailRecipients, kept.EmailEnabled, kept.NotifyOnSuccess));

        // E-mail on with nothing stored of its own: MatSMTP supplies server and sender.
        Assert.Null(server.Validate(new NotificationSettings { EmailEnabled = true, MailRecipients = "ops@example.com", NotifyOnFailure = true }));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_fixes_the_smtp_fields_only_with_the_variables(string language)
    {
        var smtp = MatSmtpSettings.From(new ConfigurationBuilder().AddInMemoryCollection(MatSmtpFactory.Variables.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build());
        var fixedDefinition = MatPlatformBuilder.Build(o => MatBuPlatform.Describe(o, smtp), language);
        var sprite = MatPlatformValidator.ReadSprite(Path.Combine(AppContext.BaseDirectory, "Fixtures", "matui-icons.svg"));
        Assert.Empty(MatPlatformValidator.Validate(fixedDefinition, sprite));

        foreach (var field in fixedDefinition.Configure)
        {
            Assert.Equal(Fixed.Contains(field.Key), field.Fixed == true);
            if (Fixed.Contains(field.Key))
            {
                Assert.Equal(smtp.Describe(language), field.Hint);
            }
        }

        Assert.All(MatPlatformBuilder.Build(MatBuPlatform.Describe, language).Configure, field => Assert.NotEqual(true, field.Fixed));
    }
}

/// <summary>MatBu with the <c>MATSMTP_*</c> variables: <c>/api/configure</c> and the settings page show the SMTP settings as fixed.</summary>
public sealed class MatSmtpSiteTests(MatSmtpFactory site) : IClassFixture<MatSmtpFactory>
{
    [Fact]
    public async Task The_definition_marks_the_smtp_fields_fixed_and_configure_answers_matsmtp()
    {
        var api = site.Api();
        using var definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        var fields = definition.RootElement.GetProperty("configure").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        foreach (var key in new[] { "smtpHost", "smtpPort", "smtpUsername", "smtpPassword", "mailFrom" })
        {
            Assert.True(fields[key].GetProperty("fixed").GetBoolean(), key);
            Assert.Contains(MatSmtpFactory.Host, fields[key].GetProperty("hint").GetString());
        }

        Assert.False(fields["emailEnabled"].TryGetProperty("fixed", out _));
        Assert.False(fields["mailRecipients"].TryGetProperty("fixed", out _));

        var body = await api.GetStringAsync("/api/configure");
        Assert.DoesNotContain(MatSmtpFactory.Password, body);
        using var configuration = JsonDocument.Parse(body);
        var values = configuration.RootElement.GetProperty("values");
        Assert.Equal(MatSmtpFactory.Host, values.GetProperty("smtpHost").GetString());
        Assert.Equal(2025, values.GetProperty("smtpPort").GetInt32());
        Assert.Equal("matbu", values.GetProperty("smtpUsername").GetString());
        Assert.True(values.GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
        Assert.Equal(MatSmtpFactory.From, values.GetProperty("mailFrom").GetString());
    }

    [Fact]
    public async Task A_change_of_a_fixed_field_is_refused_and_the_rest_stays_editable()
    {
        var api = site.Api();
        var store = site.Services.GetRequiredService<NotificationSettingsStore>();
        store.Save(new NotificationSettings { SmtpHost = "smtp.own.example", SmtpPort = 465, SmtpUsername = "own", SmtpPassword = "own-password", MailFrom = "own@example.com" });

        foreach (var change in new object[]
                 {
                     new { smtpHost = "evil.example" },
                     new { smtpPort = 25 },
                     new { smtpUsername = "evil" },
                     new { smtpPassword = "evil-password" },
                     new { mailFrom = "evil@example.com" },
                     new { emailEnabled = true, mailRecipients = "ops@example.com", smtpHost = "evil.example" },
                 })
        {
            var refused = await api.PutAsJsonAsync("/api/configure", new { values = change });
            Assert.True(refused.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(change)}: {(int)refused.StatusCode}");
        }

        var stored = store.Read();
        Assert.Equal(("smtp.own.example", 465, "own", "own-password", "own@example.com", false), (stored.SmtpHost, stored.SmtpPort, stored.SmtpUsername, stored.SmtpPassword, stored.MailFrom, stored.EmailEnabled));

        var accepted = await api.PutAsJsonAsync("/api/configure", new { values = new { emailEnabled = true, mailRecipients = "ops@example.com" } });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        stored = store.Read();
        Assert.True(stored.EmailEnabled);
        Assert.Equal("ops@example.com", stored.MailRecipients);
        Assert.Equal(("smtp.own.example", 465, "own", "own-password", "own@example.com"), (stored.SmtpHost, stored.SmtpPort, stored.SmtpUsername, stored.SmtpPassword, stored.MailFrom));

        // The mail goes through MatSMTP with its values, whatever MatBu stored for its own server.
        var client = site.Services.GetRequiredService<MailServer>().Compose(stored);
        Assert.Equal(new SmtpClientSettings(MatSmtpFactory.Host, 2025, SmtpSecurity.StartTls, "matbu", MatSmtpFactory.Password, MatSmtpFactory.From, true), client);

        Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync("/api/configure", new { values = new { emailEnabled = false } })).StatusCode);
    }

    [Fact]
    public async Task The_settings_page_shows_the_fixed_state_and_saves_nothing_for_it()
    {
        var store = site.Services.GetRequiredService<NotificationSettingsStore>();
        store.Save(new NotificationSettings { SmtpHost = "smtp.own.example", SmtpPort = 465, SmtpUsername = "own", SmtpPassword = "own-password", MailFrom = "own@example.com" }, clearPassword: true);
        var browser = await site.AdminAsync();

        var page = await browser.GetAsync("/Settings/Notifications");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var sentence = site.Services.GetRequiredService<MailServer>().Describe("de")!;
        Assert.Contains($"<p class=\"settings-help\" id=\"smtp-fixed\">{HtmlEncoder.Default.Encode(sentence)}</p>", html);
        Assert.DoesNotContain(MatSmtpFactory.Password, html);
        Assert.DoesNotContain("own-password", html);
        foreach (var field in new[] { "SmtpHost", "SmtpPort", "SmtpUsername", "SmtpPassword", "MailFrom", "SmtpUseSsl" })
        {
            Assert.Contains("disabled", Input(html, field));
        }

        Assert.Contains($"value=\"{MatSmtpFactory.Host}\"", Input(html, "SmtpHost"));
        Assert.DoesNotContain("disabled", Input(html, "MailRecipients"));
        Assert.DoesNotContain("disabled", Input(html, "EmailEnabled"));

        // A crafted post with the server fields saves only what is not fixed.
        var saved = await browser.PostAsync("/Settings/Notifications?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.EmailEnabled"] = "true",
            ["Input.MailRecipients"] = "ops@example.com",
            ["Input.NotifyOnFailure"] = "true",
            ["Input.SmtpHost"] = "evil.example",
            ["Input.SmtpPort"] = "25",
            ["Input.SmtpUsername"] = "evil",
            ["Input.SmtpPassword"] = "evil-password",
            ["Input.MailFrom"] = "evil@example.com",
        }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await saved.Content.ReadAsStringAsync();
        Assert.Contains("gespeichert", after);
        Assert.DoesNotContain("evil", after);

        var stored = store.Read();
        Assert.True(stored.EmailEnabled);
        Assert.Equal("ops@example.com", stored.MailRecipients);
        Assert.Equal(("smtp.own.example", 465, "own", "own-password", "own@example.com"), (stored.SmtpHost, stored.SmtpPort, stored.SmtpUsername, stored.SmtpPassword, stored.MailFrom));

        store.Save(new NotificationSettings(), clearPassword: true);
    }

    internal static string Input(string html, string field)
        => Regex.Match(html, $"<input[^>]*name=\"Input\\.{field}\"[^>]*>").Value is { Length: > 0 } tag ? tag : throw new Xunit.Sdk.XunitException($"No input for {field}");
}

/// <summary>MatBu without the <c>MATSMTP_*</c> variables: its own SMTP settings, editable, as before.</summary>
public sealed class OwnSmtpSiteTests(MatBuFactory site) : IClassFixture<MatBuFactory>
{
    [Fact]
    public async Task The_smtp_settings_are_editable_on_the_page_and_in_configure()
    {
        Assert.False(site.Services.GetRequiredService<MailServer>().IsFixed);

        var api = site.Api();
        using var definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        Assert.All(definition.RootElement.GetProperty("configure").EnumerateArray(), field => Assert.False(field.TryGetProperty("fixed", out _)));

        var browser = await site.AdminAsync();
        var html = await (await browser.GetAsync("/Settings/Notifications")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("smtp-fixed", html);
        Assert.DoesNotContain("MatSMTP", html);
        foreach (var field in new[] { "SmtpHost", "SmtpPort", "SmtpUsername", "SmtpPassword", "MailFrom", "SmtpUseSsl", "MailRecipients" })
        {
            Assert.DoesNotContain("disabled", MatSmtpSiteTests.Input(html, field));
        }

        var saved = await browser.PostAsync("/Settings/Notifications?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.EmailEnabled"] = "true",
            ["Input.SmtpHost"] = "smtp.own.example",
            ["Input.SmtpPort"] = "587",
            ["Input.SmtpUseSsl"] = "true",
            ["Input.SmtpUsername"] = "own",
            ["Input.SmtpPassword"] = "own-password",
            ["Input.MailFrom"] = "own@example.com",
            ["Input.MailRecipients"] = "ops@example.com",
            ["Input.NotifyOnFailure"] = "true",
        }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var store = site.Services.GetRequiredService<NotificationSettingsStore>();
        var stored = store.Read();
        Assert.Equal(("smtp.own.example", 587, "own", "own-password", "own@example.com"), (stored.SmtpHost, stored.SmtpPort, stored.SmtpUsername, stored.SmtpPassword, stored.MailFrom));
        Assert.Equal(
            new SmtpClientSettings("smtp.own.example", 587, SmtpSecurity.StartTls, "own", "own-password", "own@example.com", false),
            site.Services.GetRequiredService<MailServer>().Compose(stored));

        store.Save(new NotificationSettings(), clearPassword: true);
    }
}
