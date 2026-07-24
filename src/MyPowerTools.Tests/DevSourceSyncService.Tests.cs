using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class DevSourceSyncServiceTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-devsource", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private static string Write(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static DevSourceMapping Mapping(string name, string source, string target, string? toolId = null, params string[] patterns)
    {
        return new DevSourceMapping
        {
            Name = name,
            SourceDir = source,
            TargetDir = target,
            ToolId = toolId,
            FilePatterns = (patterns.Length == 0 ? new[] { "*" } : patterns).ToList()
        };
    }

    [Fact]
    public async Task Sync_copies_new_and_changed_files_into_the_target_directory()
    {
        var root = NewTempRoot();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            var targetDir = Path.Combine(root, "dst");
            Directory.CreateDirectory(sourceDir);
            Write(sourceDir, "Tool.Surface.dll", "v1-dll");
            Write(sourceDir, "Tool.Surface.pdb", "v1-pdb");
            Write(sourceDir, "ignore-me.txt", "noise");

            var service = new DevSourceSyncService(Path.Combine(root, "dev-source.json"));
            service.Update(settings =>
            {
                settings.Enabled = true;
                settings.Mappings.Add(Mapping("surface", sourceDir, targetDir, null, "*.dll", "*.pdb"));
            });

            var first = await service.SyncAllAsync();
            Assert.Equal(2, first.UpdatedFiles);
            Assert.Equal(0, first.SkippedFiles);
            Assert.Equal(0, first.Errors);
            Assert.Equal("v1-dll", await File.ReadAllTextAsync(Path.Combine(targetDir, "Tool.Surface.dll")));
            Assert.False(File.Exists(Path.Combine(targetDir, "ignore-me.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Sync_is_idempotent_when_nothing_changed()
    {
        var root = NewTempRoot();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            var targetDir = Path.Combine(root, "dst");
            Directory.CreateDirectory(sourceDir);
            var dll = Write(sourceDir, "Tool.Surface.dll", "stable");

            var service = new DevSourceSyncService(Path.Combine(root, "dev-source.json"));
            service.Update(settings =>
            {
                settings.Enabled = true;
                settings.Mappings.Add(Mapping("surface", sourceDir, targetDir));
            });

            await service.SyncAllAsync();
            var second = await service.SyncAllAsync();
            Assert.Equal(0, second.UpdatedFiles);
            Assert.Equal(1, second.SkippedFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Sync_recopies_when_the_source_file_changes()
    {
        var root = NewTempRoot();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            var targetDir = Path.Combine(root, "dst");
            Directory.CreateDirectory(sourceDir);
            var dll = Write(sourceDir, "Tool.Surface.dll", "v1");

            var service = new DevSourceSyncService(Path.Combine(root, "dev-source.json"));
            service.Update(settings =>
            {
                settings.Enabled = true;
                settings.Mappings.Add(Mapping("surface", sourceDir, targetDir));
            });

            await service.SyncAllAsync();
            // Simulate a fresh build with a newer write time and different content.
            File.WriteAllBytes(dll, "v2-content"u8.ToArray());
            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddSeconds(10));

            var second = await service.SyncAllAsync();
            Assert.Equal(1, second.UpdatedFiles);
            Assert.Equal("v2-content", await File.ReadAllTextAsync(Path.Combine(targetDir, "Tool.Surface.dll")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SyncForTool_only_touches_mappings_matching_the_tool_id()
    {
        var root = NewTempRoot();
        try
        {
            var sourceA = Path.Combine(root, "srcA");
            var sourceB = Path.Combine(root, "srcB");
            var targetA = Path.Combine(root, "dstA");
            var targetB = Path.Combine(root, "dstB");
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            Write(sourceA, "A.dll", "a");
            Write(sourceB, "B.dll", "b");

            var service = new DevSourceSyncService(Path.Combine(root, "dev-source.json"));
            service.Update(settings =>
            {
                settings.Enabled = true;
                settings.Mappings.Add(Mapping("a", sourceA, targetA, toolId: "tool-a"));
                settings.Mappings.Add(Mapping("b", sourceB, targetB, toolId: "tool-b"));
                settings.Mappings.Add(Mapping("shared", sourceA, targetB));
            });

            var outcome = await service.SyncForToolAsync("tool-a");
            Assert.Equal(2, outcome.UpdatedFiles); // tool-a mapping + shared (no tool id) mapping
            Assert.True(File.Exists(Path.Combine(targetA, "A.dll")));
            Assert.True(File.Exists(Path.Combine(targetB, "A.dll")));
            Assert.False(File.Exists(Path.Combine(targetB, "B.dll")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Sync_is_a_noop_when_the_global_toggle_is_off()
    {
        var root = NewTempRoot();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            var targetDir = Path.Combine(root, "dst");
            Directory.CreateDirectory(sourceDir);
            Write(sourceDir, "Tool.Surface.dll", "v1");

            var service = new DevSourceSyncService(Path.Combine(root, "dev-source.json"));
            service.Update(settings =>
            {
                settings.Enabled = false;
                settings.Mappings.Add(Mapping("surface", sourceDir, targetDir));
            });

            var outcome = await service.SyncForToolAsync(toolId: null);
            Assert.Equal(0, outcome.UpdatedFiles);
            Assert.False(Directory.Exists(targetDir) && Directory.EnumerateFiles(targetDir).Any());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Sync_reports_errors_per_mapping_without_aborting_others()
    {
        var root = NewTempRoot();
        try
        {
            var goodSource = Path.Combine(root, "good-src");
            var goodTarget = Path.Combine(root, "good-dst");
            Directory.CreateDirectory(goodSource);
            Write(goodSource, "Good.dll", "ok");

            var service = new DevSourceSyncService(Path.Combine(root, "dev-source.json"));
            service.Update(settings =>
            {
                settings.Enabled = true;
                settings.Mappings.Add(Mapping("missing", Path.Combine(root, "nope"), Path.Combine(root, "nope-dst")));
                settings.Mappings.Add(Mapping("good", goodSource, goodTarget));
            });

            var outcome = await service.SyncAllAsync();
            Assert.Equal(1, outcome.UpdatedFiles);
            Assert.Equal(1, outcome.Errors);
            Assert.Contains(outcome.Details, detail => detail.Contains("missing"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Settings_round_trip_through_the_persisted_file()
    {
        var root = NewTempRoot();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            var targetDir = Path.Combine(root, "dst");
            Directory.CreateDirectory(sourceDir);
            var path = Path.Combine(root, "dev-source.json");

            var first = new DevSourceSyncService(path);
            first.Update(settings =>
            {
                settings.Enabled = true;
                settings.SyncOnRefresh = true;
                settings.Mappings.Add(Mapping("surface", sourceDir, targetDir, toolId: "remote-notifications", "*.dll"));
            });

            var second = new DevSourceSyncService(path);
            Assert.True(second.IsEnabled);
            Assert.True(second.SyncOnRefresh);
            var mapping = Assert.Single(second.Snapshot.Mappings);
            Assert.Equal("remote-notifications", mapping.ToolId);
            Assert.Equal("*.dll", Assert.Single(mapping.FilePatterns));
        }
        finally
        {
            Cleanup(root);
        }
    }
}
