// MatPlatform 1.3.0 – a copy from MatDesign, do not edit here
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace MatPlatform;

/// <summary>How the connection to MatSMTP is secured: <c>none</c>, <c>starttls</c> (upgrade on the plain port) or <c>tls</c> (TLS from the first byte).</summary>
public enum MatSmtpTls
{
    None,
    StartTls,
    Tls,
}

/// <summary>
/// Mail through MatSMTP (MatDesign docs/PLATFORM.md, "Mail through MatSMTP"): the six <c>MATSMTP_*</c> variables Mattiverse writes into an app whose catalog entry says
/// <c>mail: {}</c>. When <see cref="IsConfigured"/> the app sends all its mail through MatSMTP with these values, and its own SMTP settings are shown as fixed, in its
/// pages and in <c>/api/configure</c> (<see cref="Fix"/>); when it is not, the app keeps its own settings as before. Read once at startup from the configuration
/// (environment variables have the same names).
/// </summary>
public sealed class MatSmtpSettings
{
    public const string HostVariable = "MATSMTP_HOST";
    public const string PortVariable = "MATSMTP_PORT";
    public const string UsernameVariable = "MATSMTP_USERNAME";
    public const string PasswordVariable = "MATSMTP_PASSWORD";
    public const string FromVariable = "MATSMTP_FROM";
    public const string TlsVariable = "MATSMTP_TLS";

    /// <summary>The port when <c>MATSMTP_PORT</c> is not set: MatSMTP's SMTP port inside the Docker network.</summary>
    public const int DefaultPort = 2025;

    /// <summary>The address of MatSMTP (on the shared Docker network its container name); null = mail through MatSMTP is off.</summary>
    public string? Host { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>The relay user of this app in MatSMTP; null = no authentication.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>The sender address the app uses for all its mail (<c>matsplit@example.com</c>); null = the app's own sender.</summary>
    public string? FromAddress { get; init; }

    public MatSmtpTls Tls { get; init; } = MatSmtpTls.None;

    /// <summary>True when <c>MATSMTP_HOST</c> is set: the app sends all its mail through MatSMTP and its own SMTP settings are fixed.</summary>
    public bool IsConfigured => Host is not null;

    /// <summary>True when a user name is set (the password goes with it).</summary>
    public bool HasCredentials => Username is not null;

    /// <summary>Plain connection, then <c>STARTTLS</c> (MailKit: <c>SecureSocketOptions.StartTls</c>; System.Net.Mail: <c>EnableSsl = true</c> on the plain port).</summary>
    public bool UseStartTls => Tls == MatSmtpTls.StartTls;

    /// <summary>TLS from the first byte (MailKit: <c>SecureSocketOptions.SslOnConnect</c>).</summary>
    public bool UseTls => Tls == MatSmtpTls.Tls;

    /// <summary>The value of <c>MATSMTP_TLS</c> as it is written: <c>none</c>, <c>starttls</c> or <c>tls</c>.</summary>
    public string TlsName => Name(Tls);

    /// <summary>The settings from the configuration. A port that is not a number from 1 to 65535 is the default; an unknown TLS mode is <c>none</c>.</summary>
    public static MatSmtpSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var port = int.TryParse(Clean(configuration[PortVariable]), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : DefaultPort;
        return new MatSmtpSettings
        {
            Host = Clean(configuration[HostVariable]),
            Port = port,
            Username = Clean(configuration[UsernameVariable]),
            Password = string.IsNullOrEmpty(configuration[PasswordVariable]) ? null : configuration[PasswordVariable],
            FromAddress = Clean(configuration[FromVariable]),
            Tls = ParseTls(configuration[TlsVariable]),
        };
    }

    /// <summary><c>none</c>, <c>starttls</c>, <c>tls</c> in any case; anything else (also empty) is <see cref="MatSmtpTls.None"/>.</summary>
    public static MatSmtpTls ParseTls(string? value) => Clean(value)?.ToLowerInvariant() switch
    {
        "starttls" => MatSmtpTls.StartTls,
        "tls" => MatSmtpTls.Tls,
        _ => MatSmtpTls.None,
    };

    public static string Name(MatSmtpTls tls) => tls switch
    {
        MatSmtpTls.StartTls => "starttls",
        MatSmtpTls.Tls => "tls",
        _ => "none",
    };

    /// <summary>
    /// One sentence for a person, in German or English, that says where the mail goes (for the hint of the fixed fields and the app's own settings page), or null when
    /// <see cref="IsConfigured"/> is false: "Mail is sent through MatSMTP (matsmtp:2025, STARTTLS) as matsplit@example.com. Mattiverse sets this; it cannot be changed here."
    /// It never contains the password.
    /// </summary>
    public string? Describe(string language = "en")
    {
        if (!IsConfigured)
        {
            return null;
        }

        var de = language != "en";
        var security = Tls switch
        {
            MatSmtpTls.StartTls => ", STARTTLS",
            MatSmtpTls.Tls => ", TLS",
            _ => string.Empty,
        };
        var where = string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}{security}");
        var sender = FromAddress is null ? string.Empty : de ? $" als {FromAddress}" : $" as {FromAddress}";
        return de
            ? $"E-Mails gehen über MatSMTP ({where}){sender}. Mattiverse legt das fest; hier lässt es sich nicht ändern."
            : $"Mail is sent through MatSMTP ({where}){sender}. Mattiverse sets this; it cannot be changed here.";
    }

    /// <summary>
    /// A field of the app's SMTP settings as <c>GET /api</c> describes it: when <see cref="IsConfigured"/>, marked <see cref="MatField.Fixed"/> (a consumer shows it without
    /// letting it be changed, and <c>PUT /api/configure</c> refuses a change) with <see cref="Describe"/> as its hint; otherwise the field as it is.
    /// </summary>
    public MatField Fix(MatField field, string language = "en")
    {
        ArgumentNullException.ThrowIfNull(field);
        return IsConfigured ? field with { Fixed = true, Required = null, Hint = Describe(language) } : field;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
