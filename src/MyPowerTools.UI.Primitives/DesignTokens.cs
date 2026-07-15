using System.Text.Json;

namespace MyPowerTools.UI;

public sealed class DesignTokens
{
    public string Version { get; init; } = "";
    public Dictionary<string, int> Spacing { get; init; } = [];
    public TokenRadius Radius { get; init; } = new();
    public TokenLayout Layout { get; init; } = new();

    public static DesignTokens Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<DesignTokens>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new DesignTokens();
    }
}

public sealed class TokenRadius
{
    public int Control { get; init; } = 6;
    public int Card { get; init; } = 8;
    public int Panel { get; init; } = 12;
    public int Window { get; init; } = 12;
}

public sealed class TokenLayout
{
    public int DashboardCardMinWidth { get; init; } = 320;
    public int DashboardCardMaxWidth { get; init; } = 420;
    public int ContentMaxWidth { get; init; } = 1180;
    public int RightPanelWidth { get; init; } = 360;
}
