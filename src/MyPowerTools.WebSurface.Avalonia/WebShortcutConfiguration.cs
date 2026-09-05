using System.Text.Json;

namespace MyPowerTools.WebSurface.Avalonia;

public sealed record WebShortcutBinding(string Gesture, bool AllowInTextInput);
public interface IConfigurableWebShortcuts
{
    void UpdateShortcutBindings(IReadOnlyList<WebShortcutBinding> bindings);
}

/// <summary>Shared per-host configuration; controls unsubscribe when their sessions are disposed.</summary>
public sealed class WebShortcutConfiguration
{
    public IReadOnlyList<WebShortcutBinding> Bindings { get; private set; } = [];
    public event Action? Changed;
    public void Update(IReadOnlyList<WebShortcutBinding> bindings)
    {
        Bindings = bindings;
        Changed?.Invoke();
    }
    public object Payload => new { type = "shortcut-bindings", bindings = Bindings.Select(item => new { gesture = item.Gesture, allowInTextInput = item.AllowInTextInput }).ToArray() };
    public string Json => JsonSerializer.Serialize(Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
