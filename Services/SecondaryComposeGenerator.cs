using System.Security.Cryptography;
using System.Text.Json;

namespace MatBu.Services;

public static class SecondaryComposeGenerator
{
    public static string GenerateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace("+", "-", StringComparison.Ordinal)
        .Replace("/", "_", StringComparison.Ordinal)
        .TrimEnd('=');

    public static string Generate(string primaryEndpoint, string instanceToken)
    {
        var endpoint = primaryEndpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !IsHttpScheme(uri.Scheme))
            throw new ArgumentException("Die Primary-Adresse muss eine absolute HTTP- oder HTTPS-URL sein.", nameof(primaryEndpoint));
        if (string.IsNullOrWhiteSpace(instanceToken))
            throw new ArgumentException("Das Instance-Token fehlt.", nameof(instanceToken));

        var yamlEndpoint = JsonSerializer.Serialize(endpoint);
        var yamlToken = JsonSerializer.Serialize(instanceToken);
        return $$"""
            name: matbu-remote-secondary

            services:
              secondary:
                image: ghcr.io/real-ttx/matbu:latest
                pull_policy: always
                restart: unless-stopped
                network_mode: bridge
                extra_hosts:
                  - "host.docker.internal:host-gateway"
                environment:
                  MATBU_INSTANCE_ROLE: Secondary
                  MATBU_PRIMARY_ENDPOINT: {{yamlEndpoint}}
                  MATBU_INSTANCE_TOKEN: {{yamlToken}}
                volumes:
                  - matbu-remote-data:/data
                  - /var/run/docker.sock:/var/run/docker.sock:ro
                healthcheck:
                  test: ["CMD", "curl", "--fail", "--silent", "http://localhost:9293/health"]
                  interval: 30s
                  timeout: 5s
                  retries: 3
                  start_period: 20s

            volumes:
              matbu-remote-data:
            """;
    }

    private static bool IsHttpScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
