using RemoteCommands.Surface.Services;

namespace MyPowerTools.Tests;

public sealed class RemoteCommandsProductTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Remote_commands_tool_manifest_routes_to_the_dotnet_surface()
    {
        var toolRoot = Path.Combine(
            Root, "tools", "remote-commands", "current-integration");
        var toolJson = File.ReadAllText(Path.Combine(
            toolRoot, "modules", "android-tools-suite", "modules", "remote-commands", "ui", "tool.json"));
        var commandsIndex = File.ReadAllText(Path.Combine(
            toolRoot, "modules", "android-tools-suite", "modules", "remote-commands", "commands.index.json"));
        var release = File.ReadAllText(Path.Combine(Root, "tools", "remote-commands", "tool-release.json"));
        var buildScript = File.ReadAllText(Path.Combine(Root, "tools", "remote-commands", "build.ps1"));
        var project = File.ReadAllText(Path.Combine(
            toolRoot, "src", "RemoteCommands.Surface", "RemoteCommands.Surface.csproj"));
        var factory = File.ReadAllText(Path.Combine(
            toolRoot, "src", "RemoteCommands.Surface", "RemoteCommandsSurfaceFactory.cs"));

        Assert.DoesNotContain("\"availability\": \"paused\"", toolJson, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"dotnet-surface\"", toolJson, StringComparison.Ordinal);
        Assert.Contains("\"assembly\": \"surface/RemoteCommands.Surface.dll\"", toolJson, StringComparison.Ordinal);
        Assert.Contains("RemoteCommands.Surface.RemoteCommandsSurfaceFactory", toolJson, StringComparison.Ordinal);
        Assert.Contains("\"routeId\": \"workspace\"", toolJson, StringComparison.Ordinal);
        Assert.Contains("\"routeId\": \"workspace\"", commandsIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("\"routeId\": \"catalog\"", commandsIndex, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"active\"", release, StringComparison.Ordinal);
        Assert.Contains("RemoteCommands.Surface.csproj", buildScript, StringComparison.Ordinal);
        Assert.Contains("Avalonia", project, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools.AvaloniaSdk", project, StringComparison.Ordinal);
        Assert.Contains("IMptAvaloniaSurfaceFactory", factory, StringComparison.Ordinal);
        Assert.Contains("RemoteCommandsViewModel", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_yaml_parser_reads_the_original_powertool_catalog()
    {
        var commands = RemoteCommandsYaml.ParseCommands(RemoteCommandsYaml.DefaultCommandsYaml);

        Assert.Equal(11, commands.Count);
        Assert.Contains(commands, command =>
            command.Id == "decode_stack" &&
            command.Label == "Decode Kernel Stack" &&
            command.Type == "shell");
        Assert.Contains(commands, command =>
            command.Id == "replace_host" &&
            command.Command == "replace_host_directory" &&
            command.Type == "py");
        Assert.Contains(commands, command =>
            command.Id == "gen_rsync_from_folders" &&
            command.Type == "py");
    }

    [Fact]
    public void Commands_yaml_exposes_user_facing_input_metadata()
    {
        const string yaml = """
            commands:
              - id: analyze
                label: Analyze
                command: /opt/analyze
                description: Analyze input
                type: shell
                input1_label: Trace data
                input1_placeholder: Paste the trace.
                input2_label: Configuration
                input2_placeholder: Paste optional configuration.
                show_second_input: true
            """;

        var command = Assert.Single(RemoteCommandsYaml.ParseCommands(yaml));

        Assert.Equal("Trace data", command.Input1Label);
        Assert.Equal("Paste the trace.", command.Input1Placeholder);
        Assert.Equal("Configuration", command.Input2Label);
        Assert.Equal("Paste optional configuration.", command.Input2Placeholder);
        Assert.True(command.ShowSecondInput);
        Assert.True(command.UsesRemoteHost);
    }

    [Fact]
    public void Commands_yaml_validation_rejects_missing_commands_duplicates_and_unknown_types()
    {
        Assert.True(RemoteCommandsYaml.TryValidate(RemoteCommandsYaml.DefaultCommandsYaml, out var error));
        Assert.Null(error);

        Assert.False(RemoteCommandsYaml.TryValidate("labels:\n  - a\n", out var missingError));
        Assert.NotNull(missingError);

        const string duplicate = """
            commands:
              - id: same
                label: First
                command: echo first
                type: shell
              - id: same
                label: Second
                command: echo second
                type: shell
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(duplicate, out var duplicateError));
        Assert.Contains("duplicated", duplicateError ?? "", StringComparison.OrdinalIgnoreCase);

        const string unknownType = """
            commands:
              - id: bad
                label: Bad
                command: echo bad
                type: custom
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(unknownType, out var typeError));
        Assert.Contains("unsupported type", typeError ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remote_commands_workspace_uses_saved_host_selector_and_command_labels()
    {
        var surfaceRoot = Path.Combine(
            Root,
            "tools",
            "remote-commands",
            "current-integration",
            "src",
            "RemoteCommands.Surface");
        var view = File.ReadAllText(Path.Combine(surfaceRoot, "Views", "RemoteCommandsView.axaml"));
        var settings = File.ReadAllText(Path.Combine(surfaceRoot, "Views", "SettingsDialog.axaml"));
        var viewModel = File.ReadAllText(Path.Combine(surfaceRoot, "ViewModels", "RemoteCommandsViewModel.cs"));

        Assert.Contains("ItemsSource=\"{Binding HostOptions}\"", view, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding Host, Mode=TwoWay}\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Input1Label}\"", view, StringComparison.Ordinal);
        Assert.Contains("Watermark=\"{Binding Input1Placeholder}\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HostsList\"", settings, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DefaultHostInput\"", settings, StringComparison.Ordinal);
        Assert.Contains("IsHostSelectionEnabled", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceholderText=\"r743\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_hosts_are_normalized_and_round_trip_with_settings()
    {
        var hosts = RemoteCommandsStore.ParseKnownHosts("r744\nr743\nR744\n invalid host \n-user");
        Assert.Equal(new[] { "r744", "r743" }, hosts);
        Assert.Equal("r744\nr743", RemoteCommandsStore.SerializeKnownHosts(hosts));

        var directory = Path.Combine(Path.GetTempPath(), $"mpt-remote-commands-hosts-{Guid.NewGuid():N}");
        try
        {
            var store = new RemoteCommandsStore(directory);
            var settings = new RemoteCommandsSettings(
                "r744",
                "code",
                false,
                100,
                "r743",
                2,
                "r744\nr743\nr744");

            store.SaveSettings(settings);
            var loaded = store.LoadSettings();

            Assert.Equal("r744", loaded.DefaultHost);
            Assert.Equal("r743", loaded.LastHost);
            Assert.Equal(new[] { "r744", "r743" }, RemoteCommandsStore.ParseKnownHosts(loaded.KnownHosts));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Cpp_comment_transform_preserves_strings_and_line_endings()
    {
        const string source = "int a = 1; // trailing\n/* block */\nchar* s = \"http://x // y\";\r\n";

        var result = RemoteCommandsTextTransforms.RemoveCppComments(source);

        Assert.Equal("int a = 1; \n\nchar* s = \"http://x // y\";\r\n", result);
    }

    [Fact]
    public void Latex_comment_transform_drops_full_line_comments_only()
    {
        var result = RemoteCommandsTextTransforms.RemoveLatexCommentLines(
            "alpha\n% comment\nbeta%\n% tail\n");

        Assert.Equal("alpha\nbeta%\n", result);
    }

    [Fact]
    public void Latex_reflow_breaks_after_commas_and_periods_but_not_inside_commands()
    {
        var result = RemoteCommandsTextTransforms.FormatLatexCommaPeriodLines(
            "\\section{A, B.} plain text, more text.");

        Assert.Contains("\\section{A, B.}", result, StringComparison.Ordinal);
        Assert.Contains("plain text,\nmore text.", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_prefix_and_rsync_transforms_match_the_original_contract()
    {
        Assert.Equal(
            "extract_result alpha\nextract_result beta",
            RemoteCommandsTextTransforms.AddExtractResultPrefix("alpha\nbeta"));

        var rsync = RemoteCommandsTextTransforms.GenerateRsyncCommands(" /a/b \n/c ");
        Assert.StartsWith("rsync -avP r743-autodroid:/a/b $aosp_host_working_dir/", rsync, StringComparison.Ordinal);
        Assert.Contains("postconditions_db", rsync, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_directory_transform_rewrites_the_legacy_path()
    {
        Assert.Equal(
            "open http://r743.ipads-lab.se.sjtu.edu.cn:7112/out",
            RemoteCommandsTextTransforms.ReplaceHostDirectory(
                "open /home/lixr/aosp_host_working_dir/out"));
    }

    [Fact]
    public void Store_seeds_commands_yaml_and_round_trips_settings_and_history()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mpt-remote-commands-tests-{Guid.NewGuid():N}");
        try
        {
            var store = new RemoteCommandsStore(directory);
            store.EnsureInitialized();

            Assert.True(File.Exists(Path.Combine(directory, "commands.yaml")));
            Assert.NotEmpty(store.LoadCommands());

            var settings = new RemoteCommandsSettings("r743", "code", true, 50, "r744", 3, "r743\nr744");
            store.SaveSettings(settings);
            Assert.Equal(settings, store.LoadSettings());

            var retention = 10;
            for (var i = 0; i < 15; i++)
            {
                store.AppendHistory(
                    new RemoteCommandHistoryItem(
                        $"2026-08-06 10:0{i}:00",
                        $"Command {i}",
                        "echo hi",
                        "shell",
                        "r743",
                        "",
                        "",
                        false,
                        "output"),
                    retention);
            }

            var history = store.LoadHistory();
            Assert.Equal(retention, history.Count);
            Assert.Equal("Command 14", history[0].Label);

            store.ClearHistory();
            Assert.Empty(store.LoadHistory());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
    }
}
