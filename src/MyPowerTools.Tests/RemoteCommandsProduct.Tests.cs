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
        Assert.Contains("YamlDotNet", project, StringComparison.Ordinal);
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
            command.Type == "shell" &&
            command.Runner == RemoteCommandRunners.Ssh &&
            command.Inputs.Count == 2);
        Assert.Contains(commands, command =>
            command.Id == "replace_host" &&
            command.Command == "replace_host_directory" &&
            command.Type == "py" &&
            command.Runner == RemoteCommandRunners.Transform);
        Assert.Contains(commands, command =>
            command.Id == "gen_rsync_from_folders" &&
            command.Type == "py");
    }

    [Fact]
    public void Schema_two_supports_dynamic_inputs_and_local_external_tools()
    {
        const string yaml = """
            schema: 2
            defaults:
              timeout_seconds: 45
            commands:
              - id: local_tool
                label: Local tool
                group: Custom
                runner: local
                command: python
                arguments:
                  - tool.py
                  - --source
                  - "{{input:source:file}}"
                  - --mode
                  - "{{input:mode:text}}"
                inputs:
                  - id: source
                    label: Source
                    kind: multiline
                    required: true
                  - id: mode
                    label: Mode
                    kind: text
                    default: summary
                tags: [custom, analyzer]
            """;

        var command = Assert.Single(RemoteCommandsYaml.ParseCommands(yaml));

        Assert.Equal(RemoteCommandRunners.Local, command.Runner);
        Assert.Equal("Custom", command.GroupLabel);
        Assert.Equal(45, command.TimeoutSeconds);
        Assert.Equal(2, command.Inputs.Count);
        Assert.Equal("summary", command.Inputs[1].DefaultValue);
        Assert.Contains("source", RemoteCommandTemplate.GetFileInputIds(command));

        var arguments = RemoteCommandTemplate.RenderLocalArguments(
            command,
            new Dictionary<string, string> { ["source"] = "payload", ["mode"] = "full" },
            new Dictionary<string, string> { ["source"] = "/tmp/source.txt" });
        Assert.Equal(new[] { "tool.py", "--source", "/tmp/source.txt", "--mode", "full" }, arguments);
    }

    [Fact]
    public void Schema_two_supports_catalog_host_defaults_and_zero_input_commands()
    {
        const string yaml = """
            schema: 2
            defaults:
              host: build-box
            commands:
              - id: health_check
                label: Health check
                runner: ssh
                command: /usr/bin/true
              - id: pinned_check
                label: Pinned check
                runner: ssh
                host: user@other-box
                command: /usr/bin/true
                inputs: []
            """;

        var commands = RemoteCommandsYaml.ParseCommands(yaml);

        Assert.Equal(2, commands.Count);
        Assert.Empty(commands[0].Inputs);
        Assert.Equal("build-box", commands[0].CatalogDefaultHost);
        Assert.Equal("user@other-box", commands[1].Host);
        Assert.Equal("build-box", commands[1].CatalogDefaultHost);
    }

    [Fact]
    public void Legacy_catalog_entries_without_ids_remain_loadable()
    {
        const string yaml = """
            commands:
              - label: Example Analyzer
                command: /opt/tools/analyze
                type: shell
                host: r743
            """;

        var command = Assert.Single(RemoteCommandsYaml.ParseCommands(yaml));

        Assert.StartsWith("example-analyzer-", command.Id, StringComparison.Ordinal);
        Assert.True(command.LegacyFileArguments);
        Assert.Equal(new[] { "input1", "input2" }, command.Inputs.Select(input => input.Id));
    }

    [Fact]
    public void Commands_yaml_validation_reports_structural_and_reference_errors()
    {
        Assert.True(RemoteCommandsYaml.TryValidate(RemoteCommandsYaml.DefaultCommandsYaml, out var error));
        Assert.Null(error);

        Assert.False(RemoteCommandsYaml.TryValidate("labels:\n  - a\n", out var missingError));
        Assert.Contains("commands", missingError, StringComparison.OrdinalIgnoreCase);

        const string unknownInput = """
            schema: 2
            commands:
              - id: broken
                label: Broken
                runner: local
                command: echo
                arguments: ["{{input:missing:text}}"]
                inputs: []
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(unknownInput, out var referenceError));
        Assert.Contains("unknown input", referenceError, StringComparison.OrdinalIgnoreCase);

        const string duplicateKey = """
            schema: 2
            commands:
              - id: duplicate
                id: duplicate_again
                label: Duplicate
                runner: local
                command: echo
                inputs: []
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(duplicateKey, out var duplicateError));
        Assert.Contains("duplicate", duplicateError, StringComparison.OrdinalIgnoreCase);

        const string shellPlaceholderWithoutArguments = """
            schema: 2
            commands:
              - id: unsafe_local_shell
                label: Unsafe local shell
                runner: local
                command: echo {{input:value:text}}
                inputs:
                  - id: value
                    label: Value
                    kind: text
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(shellPlaceholderWithoutArguments, out var localError));
        Assert.Contains("arguments", localError, StringComparison.OrdinalIgnoreCase);

        const string malformedPlaceholder = """
            schema: 2
            commands:
              - id: malformed
                label: Malformed
                runner: ssh
                command: /usr/bin/printf
                arguments: ["{{input:value:bytes}}"]
                inputs:
                  - id: value
                    label: Value
                    kind: text
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(malformedPlaceholder, out var placeholderError));
        Assert.Contains("placeholder", placeholderError, StringComparison.OrdinalIgnoreCase);

        const string invalidCatalogHost = """
            schema: 2
            defaults:
              host: -oProxyCommand=bad
            commands:
              - id: host_check
                label: Host check
                runner: ssh
                command: /usr/bin/true
                inputs: []
            """;
        Assert.False(RemoteCommandsYaml.TryValidate(invalidCatalogHost, out var hostError));
        Assert.Contains("defaults.host", hostError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_parser_accepts_whitespace_around_input_marker()
    {
        const string yaml = """
            schema: 2
            commands:
              - id: spaced_placeholder
                label: Spaced placeholder
                runner: local
                command: echo
                arguments: ["{{ input : value : text }}"]
                inputs:
                  - id: value
                    label: Value
                    kind: text
            """;

        var command = Assert.Single(RemoteCommandsYaml.ParseCommands(yaml));
        var arguments = RemoteCommandTemplate.RenderLocalArguments(
            command,
            new Dictionary<string, string> { ["value"] = "hello" },
            new Dictionary<string, string>());

        Assert.Equal(new[] { "hello" }, arguments);
    }

    [Fact]
    public void Shell_template_quotes_each_argument_and_environment_value()
    {
        var command = new RemoteCommandDefinition(
            Id: "example",
            Label: "Example",
            Command: "/opt/my tool",
            Description: "",
            Runner: RemoteCommandRunners.Ssh,
            Group: "",
            Host: "",
            TimeoutSeconds: 30,
            Inputs: [new RemoteCommandInputDefinition("value", "Value", "", "text", true)],
            Arguments: ["--value", "{{input:value:text}}", "static value"],
            Tags: [],
            Environment: new Dictionary<string, string> { ["MODE"] = "safe mode" });

        var rendered = RemoteCommandTemplate.BuildShellCommand(
            command,
            new Dictionary<string, string> { ["value"] = "a'b" },
            new Dictionary<string, string>());

        Assert.Contains("MODE='safe mode'", rendered, StringComparison.Ordinal);
        Assert.Contains("'/opt/my tool'", rendered, StringComparison.Ordinal);
        Assert.Contains("'a'\"'\"'b'", rendered, StringComparison.Ordinal);
        Assert.Contains("'static value'", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("r743")]
    [InlineData("user@example-host")]
    [InlineData("[2001:db8::1]")]
    public void Ssh_destination_validation_accepts_safe_destinations(string destination)
    {
        Assert.True(RemoteCommandExecutionService.IsValidSshDestination(destination, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("-oProxyCommand=bad")]
    [InlineData("host name")]
    [InlineData("host:/tmp")]
    [InlineData("")]
    public void Ssh_destination_validation_rejects_option_and_path_injection(string destination)
    {
        Assert.False(RemoteCommandExecutionService.IsValidSshDestination(destination, out var error));
        Assert.NotNull(error);
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
    public void Store_seeds_catalog_and_round_trips_extended_settings_and_history()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mpt-remote-commands-tests-{Guid.NewGuid():N}");
        try
        {
            var store = new RemoteCommandsStore(directory);
            store.EnsureInitialized();

            Assert.True(File.Exists(Path.Combine(directory, "commands.yaml")));
            Assert.NotEmpty(store.LoadCommands());

            var settings = new RemoteCommandsSettings(
                "r743",
                "code",
                true,
                50,
                "r744",
                3,
                LastCommandId: "decode_stack",
                ShowHistory: false);
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
                        "local",
                        "",
                        "legacy input",
                        "",
                        false,
                        "output",
                        CommandId: $"command-{i}",
                        Inputs: new Dictionary<string, string> { ["source"] = $"input-{i}" },
                        Succeeded: i % 2 == 0,
                        ExitCode: i % 2 == 0 ? 0 : 1,
                        DurationMilliseconds: 1500),
                    retention);
            }

            var history = store.LoadHistory();
            Assert.Equal(retention, history.Count);
            Assert.Equal("Command 14", history[0].Label);
            Assert.Equal("input-14", history[0].EffectiveInputs["source"]);
            Assert.Equal("1.5 s", history[0].DurationText);

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
