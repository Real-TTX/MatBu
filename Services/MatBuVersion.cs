using System.Reflection;

namespace MatBu.Services;

/// <summary>The version of MatBu (<c>Version</c> in MatBu.csproj), without the build metadata after <c>+</c>.</summary>
public static class MatBuVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(MatBuVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? typeof(MatBuVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
