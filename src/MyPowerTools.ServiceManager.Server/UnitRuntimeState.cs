using System.Text.Json;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Persisted runtime state for a single unit, used to re-adopt a still-running process
/// after the ServiceManager itself restarts. Written whenever the supervised process starts;
/// cleared when the unit is explicitly stopped (not when it crashes — crashes may auto-restart).
/// </summary>
public sealed record UnitRuntimeState(
    string UnitId,
    int Pid,
    string InstanceToken,
    DateTimeOffset StartedAt,
    int RestartCount,
    string ProcessStartTimeIso)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static UnitRuntimeState? FromJson(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UnitRuntimeState>(json);
}
