// MatPlatform 1.4.0 – a copy from MatDesign, do not edit here
using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatPlatform;

/// <summary>
/// The checks of <c>PUT /api/configure</c> that the definition states (docs/PLATFORM.md): unknown keys, the type of each value, <c>required</c>,
/// <c>min</c> / <c>max</c> (a number, or the length of a text), <c>pattern</c>, <c>options</c>, an address for <c>url</c> and <c>email</c>.
/// A field hidden by <c>showIf</c> is not required; a <c>fixed</c> field cannot be changed. Messages are German or English.
/// </summary>
public static class MatValueChecker
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The errors per key of <paramref name="changes"/> against <paramref name="definition"/>; <paramref name="current"/> are the values before the change.</summary>
    public static IReadOnlyDictionary<string, string> Check(
        MatApiDefinition definition,
        IReadOnlyDictionary<string, JsonElement> changes,
        IReadOnlyDictionary<string, object?> current,
        string language = "en")
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(current);

        string T(string de, string en) => language == "en" ? en : de;
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = definition.Configure.ToDictionary(f => f.Key, StringComparer.Ordinal);

        // The values after the change, as JSON: what showIf and required look at.
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in current)
        {
            merged[key] = JsonSerializer.SerializeToElement(value, MatApiDefinition.Json);
        }

        foreach (var (key, value) in changes)
        {
            merged[key] = value;
        }

        foreach (var (key, value) in changes)
        {
            if (!fields.TryGetValue(key, out var field))
            {
                errors[key] = T("Diese Einstellung gibt es nicht.", "There is no such setting.");
                continue;
            }

            if (field.Fixed == true)
            {
                errors[key] = T("Diese Einstellung ist von außen fest vorgegeben.", "This setting is fixed from outside the app.");
                continue;
            }

            if (CheckValue(field, value, T) is { } error)
            {
                errors[key] = error;
            }
        }

        foreach (var field in definition.Configure.Where(f => f.Required == true && !errors.ContainsKey(f.Key) && IsShown(f, merged)))
        {
            if (!merged.TryGetValue(field.Key, out var value) || IsEmpty(value))
            {
                errors[field.Key] = T("Bitte einen Wert angeben.", "A value is required.");
            }
        }

        return errors;
    }

    /// <summary>True when every field that <see cref="MatField.ShowIf"/> names has the value given there.</summary>
    public static bool IsShown(MatField field, IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);

        return field.ShowIf is null
            || field.ShowIf.All(c => values.TryGetValue(c.Key, out var value) && JsonElement.DeepEquals(value, c.Value));
    }

    private static string? CheckValue(MatField field, JsonElement value, Func<string, string, string> T)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            // Null clears a value; whether it may be empty is the check of required.
            return field.Type is "number" or "bool" && field.Required == true ? T("Bitte einen Wert angeben.", "A value is required.") : null;
        }

        switch (field.Type)
        {
            case "bool":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : T("Bitte true oder false angeben.", "Send true or false.");

            case "number":
                if (value.ValueKind != JsonValueKind.Number)
                {
                    return T("Bitte eine Zahl angeben.", "Send a number.");
                }

                var number = value.GetDouble();
                return OutOf(number, field)
                    ? T($"Bitte einen Wert {Range(field, "de")} angeben.", $"The value must be {Range(field, "en")}.")
                    : null;

            default:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return T("Bitte einen Text angeben.", "Send a text.");
                }

                var text = value.GetString()!;
                if (text.Length == 0)
                {
                    return null;
                }

                if (OutOf(text.Length, field))
                {
                    return T($"Bitte {Range(field, "de")} Zeichen angeben.", $"The text must have {Range(field, "en")} characters.");
                }

                if (field.Pattern is { Length: > 0 } pattern && !Matches(text, pattern))
                {
                    return T("Der Wert hat nicht die erwartete Form.", "The value does not have the expected form.");
                }

                return field.Type switch
                {
                    "select" when field.Options is { } options && options.All(o => o.Value != text) => T("Bitte einen der angebotenen Werte wählen.", "Choose one of the offered values."),
                    "url" when !(Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) => T("Bitte eine Adresse mit http:// oder https:// angeben.", "Send an address with http:// or https://."),
                    "email" when !(MailAddress.TryCreate(text, out var mail) && mail.Address == text) => T("Bitte eine E-Mail-Adresse angeben.", "Send an e-mail address."),
                    _ => null,
                };
        }
    }

    private static bool OutOf(double value, MatField field) => (field.Min is { } min && value < min) || (field.Max is { } max && value > max);

    private static string Range(MatField field, string language)
    {
        string N(double value) => value.ToString(CultureInfo.InvariantCulture);
        return (field.Min, field.Max, language) switch
        {
            ({ } min, { } max, "de") => $"zwischen {N(min)} und {N(max)}",
            ({ } min, { } max, _) => $"between {N(min)} and {N(max)}",
            ({ } min, null, "de") => $"ab {N(min)}",
            ({ } min, null, _) => $"at least {N(min)}",
            (null, { } max, "de") => $"bis {N(max)}",
            (null, { } max, _) => $"at most {N(max)}",
            _ => string.Empty,
        };
    }

    /// <summary>The whole text matches, like the <c>pattern</c> attribute of a browser; a pattern that is no regular expression lets everything through.</summary>
    private static bool Matches(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => value.GetString()!.Length == 0,
        JsonValueKind.Object => value.TryGetProperty("isSet", out var set) && set.ValueKind == JsonValueKind.False,
        _ => false,
    };
}
