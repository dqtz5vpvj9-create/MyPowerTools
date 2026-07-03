using MyPowerTools.Runtime;
using System.Text.Json.Nodes;

namespace MyPowerTools.ModuleHost.Http;

public sealed class HttpModuleHost
{
    private readonly HttpClient _httpClient;

    public HttpModuleHost(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<ModuleStatusSnapshot> GetStatusAsync(string moduleId, SelectedEntrypoint entrypoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entrypoint.EndpointAddress))
        {
            return Degraded(moduleId, "HTTP endpoint is missing.");
        }

        try
        {
            var uri = new Uri(new Uri(entrypoint.EndpointAddress.TrimEnd('/') + "/"), "api/status");
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ModuleStatusSnapshot(
                moduleId,
                response.IsSuccessStatusCode ? "running" : "degraded",
                response.IsSuccessStatusCode ? "HTTP facade health endpoint is reachable." : $"HTTP facade returned {(int)response.StatusCode}.",
                DateTimeOffset.UtcNow,
                [new HealthCheckSnapshot("http", "HTTP health", response.IsSuccessStatusCode, LogRouter.Redact(body))],
                0);
        }
        catch (Exception ex)
        {
            return Degraded(moduleId, ex.Message);
        }
    }

    public string Describe(SelectedEntrypoint entrypoint)
    {
        return $"HTTP facade selected at {entrypoint.EndpointAddress ?? entrypoint.Command ?? "unknown endpoint"}.";
    }

    private static ModuleStatusSnapshot Degraded(string moduleId, string message)
    {
        return new ModuleStatusSnapshot(
            moduleId,
            "degraded",
            message,
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("http", "HTTP health", false, LogRouter.Redact(message))],
            0);
    }
}
