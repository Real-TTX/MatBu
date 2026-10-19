using MailKit.Security;
using MatPlatform;

namespace MatBu.Services;

/// <summary>How the connection to the SMTP server is secured.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain SMTP.</summary>
    None,

    /// <summary>Plain connection, then <c>STARTTLS</c> (required).</summary>
    StartTls,

    /// <summary>TLS from the first byte.</summary>
    Tls,
}

/// <summary>Everything the mail client needs for one message: where it connects, how, as whom, and the sender.</summary>
public sealed record SmtpClientSettings(string Host, int Port, SmtpSecurity Security, string? Username, string? Password, string From, bool ThroughMatSmtp)
{
    public SecureSocketOptions SocketOptions => Security switch
    {
        SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}

/// <summary>
/// Which SMTP server MatBu's mail goes to. When Mattiverse provides MatSMTP (<c>MATSMTP_*</c>, MatDesign docs/PLATFORM.md "Mail through MatSMTP"),
/// all mail goes through it and the server settings (host, port, encryption, user, password, sender) are fixed: shown, not editable, nothing saved for them;
/// what MatBu stored for its own server stays stored and is used again when the variables are gone. Whether e-mail is on and the recipients stay MatBu's own.
/// Without the variables MatBu uses its own settings exactly as before.
/// </summary>
public sealed class MailServer(MatSmtpSettings matSmtp)
{
    /// <summary>The keys of <c>/api/configure</c> that MatSMTP fixes.</summary>
    public static readonly IReadOnlySet<string> FixedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "smtpHost", "smtpPort", "smtpUsername", "smtpPassword", "mailFrom",
    };

    public MatSmtpSettings MatSmtp => matSmtp;

    /// <summary>True when the mail goes through MatSMTP and the server settings cannot be changed in MatBu.</summary>
    public bool IsFixed => matSmtp.IsConfigured;

    /// <summary>The sentence for the settings page and the hint of the fixed fields (German or English), or null when the settings are MatBu's own.</summary>
    public string? Describe(string language = "de") => matSmtp.Describe(language);

    /// <summary>The settings in force: <paramref name="stored"/> with MatSMTP's server, port, encryption, user, password and sender when it is configured.</summary>
    public NotificationSettings InForce(NotificationSettings stored)
    {
        var copy = Copy(stored);
        if (!IsFixed) return copy;
        copy.SmtpHost = matSmtp.Host!;
        copy.SmtpPort = matSmtp.Port;
        copy.SmtpUseSsl = matSmtp.Tls != MatSmtpTls.None;
        copy.SmtpUsername = matSmtp.Username ?? "";
        copy.SmtpPassword = matSmtp.Password ?? "";
        copy.MailFrom = matSmtp.FromAddress ?? stored.MailFrom;
        return copy;
    }

    /// <summary>
    /// What to save from <paramref name="input"/>: when MatSMTP is configured, its server fields are replaced by the <paramref name="stored"/> ones (and the password
    /// is left empty, which keeps the stored one), so a change of them is never saved; otherwise <paramref name="input"/> as it is.
    /// </summary>
    public NotificationSettings KeepFixed(NotificationSettings input, NotificationSettings stored)
    {
        if (!IsFixed) return input;
        var copy = Copy(input);
        copy.SmtpHost = stored.SmtpHost;
        copy.SmtpPort = stored.SmtpPort;
        copy.SmtpUseSsl = stored.SmtpUseSsl;
        copy.SmtpUsername = stored.SmtpUsername;
        copy.SmtpPassword = "";
        copy.MailFrom = stored.MailFrom;
        return copy;
    }

    /// <summary>The first problem of <paramref name="settings"/> as they would be in force, or null (<see cref="NotificationSettingsRules.Validate"/>).</summary>
    public string? Validate(NotificationSettings settings) => NotificationSettingsRules.Validate(InForce(settings));

    /// <summary>The client settings for a message: MatSMTP's when it is configured, else MatBu's own (<c>SmtpUseSsl</c> = STARTTLS, as System.Net.Mail did).</summary>
    public SmtpClientSettings Compose(NotificationSettings own)
    {
        if (IsFixed)
        {
            var security = matSmtp.Tls switch
            {
                MatSmtpTls.Tls => SmtpSecurity.Tls,
                MatSmtpTls.StartTls => SmtpSecurity.StartTls,
                _ => SmtpSecurity.None,
            };
            return new SmtpClientSettings(
                matSmtp.Host!,
                matSmtp.Port,
                security,
                matSmtp.HasCredentials ? matSmtp.Username : null,
                matSmtp.HasCredentials ? matSmtp.Password ?? "" : null,
                matSmtp.FromAddress ?? own.MailFrom,
                ThroughMatSmtp: true);
        }

        var hasUser = !string.IsNullOrWhiteSpace(own.SmtpUsername);
        return new SmtpClientSettings(
            own.SmtpHost,
            own.SmtpPort,
            own.SmtpUseSsl ? SmtpSecurity.StartTls : SmtpSecurity.None,
            hasUser ? own.SmtpUsername : null,
            hasUser ? own.SmtpPassword : null,
            own.MailFrom,
            ThroughMatSmtp: false);
    }

    private static NotificationSettings Copy(NotificationSettings source) => new()
    {
        WebhookEnabled = source.WebhookEnabled,
        WebhookUrl = source.WebhookUrl,
        EmailEnabled = source.EmailEnabled,
        SmtpHost = source.SmtpHost,
        SmtpPort = source.SmtpPort,
        SmtpUseSsl = source.SmtpUseSsl,
        SmtpUsername = source.SmtpUsername,
        SmtpPassword = source.SmtpPassword,
        MailFrom = source.MailFrom,
        MailRecipients = source.MailRecipients,
        NotifyOnSuccess = source.NotifyOnSuccess,
        NotifyOnFailure = source.NotifyOnFailure,
        ActivationDate = source.ActivationDate,
    };
}
