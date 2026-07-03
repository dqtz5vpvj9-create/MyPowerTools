namespace MyPowerTools.Runtime;

public sealed class HealthMonitor
{
    private readonly HttpClient _httpClient;

    public HealthMonitor(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task<ModuleStatusSnapshot> CheckAsync(RuntimeModuleRecord record, CancellationToken cancellationToken)
    {
        if (record.Entrypoint is null)
        {
            return record.Status;
        }

        if (record.Entrypoint.Kind == "http")
        {
            return await CheckHttpAsync(record, cancellationToken);
        }

        return record.Status;
    }

    private async Task<ModuleStatusSnapshot> CheckHttpAsync(RuntimeModuleRecord record, CancellationToken cancellationToken)
    {
        var endpoint = record.Entrypoint?.EndpointAddress;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Degraded(record, "HTTP endpoint is missing.");
        }

        var healthPath = record.Entrypoint?.HealthPath ?? "/api/status";
        try
        {
            var uri = new Uri(new Uri(endpoint.TrimEnd('/') + "/"), healthPath.TrimStart('/'));
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ModuleStatusSnapshot(
                record.Module.Manifest.Id,
                response.IsSuccessStatusCode ? "running" : "degraded",
                response.IsSuccessStatusCode ? "HTTP health check succeeded." : $"HTTP health returned {(int)response.StatusCode}.",
                DateTimeOffset.UtcNow,
                [
                    new HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                    new HealthCheckSnapshot("http", "HTTP health", response.IsSuccessStatusCode, LogRouter.Redact(body))
                ],
                record.Status.EventSeq);
        }
        catch (Exception ex)
        {
            return Degraded(record, ex.Message);
        }
    }

    private static ModuleStatusSnapshot Degraded(RuntimeModuleRecord record, string message)
    {
        return new ModuleStatusSnapshot(
            record.Module.Manifest.Id,
            "degraded",
            LogRouter.Redact(message),
            DateTimeOffset.UtcNow,
            [
                new HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new HealthCheckSnapshot("http", "HTTP health", false, LogRouter.Redact(message))
            ],
            record.Status.EventSeq);
    }
}
