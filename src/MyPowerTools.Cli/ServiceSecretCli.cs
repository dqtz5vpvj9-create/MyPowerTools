using System.Text.Json;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;

namespace MyPowerTools.Cli;

public static class ServiceSecretCli
{
    public sealed record Entry(string ModuleId, string Name, string Value);
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !args.SequenceEqual(new[] { "import", "--stdin" }))
            {
                Console.Error.WriteLine("Linux: mpt secrets import --stdin (JSON array of moduleId, name, value; values are never printed)");
                return 2;
            }
            var entries = JsonSerializer.Deserialize<Entry[]>(await Console.In.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Missing entries.");
            var store = new LinuxServiceSecretStore();
            var references = new HashSet<string>(StringComparer.Ordinal);
            // Validate the entire input and existing entries before writing any value.
            foreach (var entry in entries)
            {
                var reference = SecretReference.Create(entry.ModuleId, entry.Name);
                ArgumentNullException.ThrowIfNull(entry.Value);
                if (!references.Add(reference.Uri)) throw new ArgumentException("Duplicate reference.");
                var existing = await store.ReadAsync(reference, default);
                if (existing is not null && existing != entry.Value) throw new InvalidOperationException("Existing credential differs.");
            }
            foreach (var entry in entries)
            {
                var reference = await store.SaveAsync(entry.ModuleId, entry.Name, entry.Value, default);
                if (await store.ReadAsync(reference, default) != entry.Value) throw new IOException("Credential migration failed.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, imported = entries.Length }));
            return 0;
        }
        catch
        {
            // JSON/parser/IO errors can contain credential fragments or paths.
            Console.Error.WriteLine("Credential import failed. Check the input, vault permissions and conflicting entries. Existing desktop credentials were not removed.");
            return 1;
        }
    }
}
