using Microsoft.Win32;
using NssmManager.Compatibility;
using NssmManager.Contracts;

namespace NssmManager.Tests;

public sealed class NssmRegistryTranslationTests
{
    [Fact]
    public void service_registry_path_matches_upstream_shape()
    {
        Assert.Equal(1, Math.Sign(NssmRegistry.service_registry_path("Service", false, null, 255, out var servicePath)));
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\Service", servicePath);
        Assert.True(NssmRegistry.service_registry_path("Service", true, "AppExit", 255, out var exitPath) > 0);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\Service\Parameters\AppExit", exitPath);
        Assert.Equal(-1, NssmRegistry.service_registry_path(new string('x', 250), true, null, 255, out _));
    }

    [Fact]
    public void open_registry_key_honours_must_exist()
    {
        var path = $@"SOFTWARE\MyPowerTools\DefinitelyMissing\{Guid.NewGuid():N}";
        Assert.Equal(NssmRegistry.ErrorFileNotFound, NssmRegistry.open_registry_key(path, NssmRegistry.KeyRead, out var key, false));
        Assert.Null(key);
    }

    [Fact]
    public void enumerate_registry_values_advances_only_on_success() => WithTemporaryKey(key =>
    {
        key.SetValue("A", 1, RegistryValueKind.DWord);
        uint index = 0;
        Assert.Equal(0, NssmRegistry.enumerate_registry_values(key, ref index, 256, out var name));
        Assert.Equal("A", name);
        Assert.Equal(1u, index);
        Assert.Equal(NssmRegistry.ErrorNoMoreItems, NssmRegistry.enumerate_registry_values(key, ref index, 256, out _));
        Assert.Equal(1u, index);
    });

    [Fact]
    public void create_parameters_writes_upstream_types()
    {
        if (!RegistryMutationEnabled()) return;
        var name = "NssmTranslation_" + Guid.NewGuid().ToString("N");
        var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
        try
        {
            using (Registry.LocalMachine.CreateSubKey(path, writable: true)) { }
            var configuration = new NssmServiceConfiguration
            {
                Name = name,
                Application = @"C:\Windows\System32\cmd.exe",
                AppDirectory = @"C:\Windows\System32",
                Priority = "HIGH_PRIORITY_CLASS"
            };
            Assert.Equal(0, NssmRegistry.create_parameters(configuration, editing: false));
            using var parameters = Registry.LocalMachine.OpenSubKey(path + @"\Parameters");
            Assert.Equal(RegistryValueKind.ExpandString, parameters!.GetValueKind("Application"));
            Assert.Equal(RegistryValueKind.DWord, parameters.GetValueKind("AppPriority"));
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void create_exit_action_uses_unnamed_value()
    {
        if (!RegistryMutationEnabled()) return;
        var name = "NssmTranslation_" + Guid.NewGuid().ToString("N");
        var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
        try
        {
            using (Registry.LocalMachine.CreateSubKey(path, writable: true)) { }
            Assert.Equal(0, NssmRegistry.create_exit_action(name, "Ignore", editing: true));
            using var key = Registry.LocalMachine.OpenSubKey(path + @"\Parameters\AppExit");
            Assert.Equal("Ignore", key!.GetValue(""));
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, false);
        }
    }

    [Fact]
    public void get_environment_requires_multi_string() => WithTemporaryKey(key =>
    {
        key.SetValue("Good", new[] { "A=1" }, RegistryValueKind.MultiString);
        Assert.Equal(0, NssmRegistry.get_environment("service", key, "Good", out var block, out var length));
        Assert.Equal(new[] { "A=1" }, NssmDoubleNull.ToStrings(block, length));
        key.SetValue("Bad", "A=1", RegistryValueKind.String);
        Assert.Equal(2, NssmRegistry.get_environment("service", key, "Bad", out _, out _));
    });

    [Fact]
    public void get_string_honours_expand_sanitise_and_missing() => WithTemporaryKey(key =>
    {
        var variable = "NSSM_TRANSLATION_VALUE";
        Environment.SetEnvironmentVariable(variable, "expanded");
        try
        {
            key.SetValue("Path", $"\"%{variable}%\\file\"", RegistryValueKind.ExpandString);
            Assert.Equal(0, NssmRegistry.get_string(key, "Path", 1024, false, true, true, out var raw));
            Assert.Equal($"%{variable}%\\file", raw);
            Assert.Equal(0, NssmRegistry.expand_parameter(key, "Path", 1024, true, out var expanded));
            Assert.Equal(@"expanded\file", expanded);
            Assert.Equal(0, NssmRegistry.get_string(key, "Missing", 1024, false, false, false, out var missing));
            Assert.Equal("", missing);
            Assert.Equal(2, NssmRegistry.get_string(key, "Missing", 1024, false, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    });

    [Fact]
    public void set_string_preserves_registry_kind() => WithTemporaryKey(key =>
    {
        Assert.Equal(0, NssmRegistry.set_string(key, "String", "value"));
        Assert.Equal(0, NssmRegistry.set_expand_string(key, "Expand", "%TEMP%"));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("String"));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Expand"));
    });

    [Fact]
    public void get_and_set_number_match_dword_contract() => WithTemporaryKey(key =>
    {
        Assert.Equal(0, NssmRegistry.set_number(key, "Number", uint.MaxValue));
        Assert.Equal(1, NssmRegistry.get_number(key, "Number", out var number));
        Assert.Equal(uint.MaxValue, number);
        Assert.Equal(0, NssmRegistry.get_number(key, "Missing", out _, false));
        Assert.Equal(-1, NssmRegistry.get_number(key, "Missing", out _));
        key.SetValue("Wrong", "1", RegistryValueKind.String);
        Assert.Equal(-2, NssmRegistry.get_number(key, "Wrong", out _));
    });

    [Fact]
    public void override_milliseconds_uses_default_for_invalid_value() => WithTemporaryKey(key =>
    {
        uint value = 1;
        NssmRegistry.override_milliseconds("service", key, "Missing", ref value, 1500, 0);
        Assert.Equal(1500u, value);
        key.SetValue("Delay", 25, RegistryValueKind.DWord);
        NssmRegistry.override_milliseconds("service", key, "Delay", ref value, 1500, 0);
        Assert.Equal(25u, value);
    });

    [Fact]
    public void get_io_parameters_applies_nssm_defaults() => WithTemporaryKey(key =>
    {
        var service = new NssmServiceConfiguration();
        Assert.Equal(0, NssmRegistry.get_io_parameters(ref service, key));
        Assert.Equal(2u, service.AppStdinShareMode);
        Assert.Equal(3u, service.AppStdoutShareMode);
        Assert.Equal(4u, service.AppStdoutCreationDisposition);
        Assert.Equal(128u, service.AppStderrFlagsAndAttributes);
    });

    [Fact]
    public void get_parameters_reads_upstream_types()
    {
        if (!RegistryMutationEnabled()) return;
        var name = "NssmTranslation_" + Guid.NewGuid().ToString("N");
        var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
        try
        {
            using (Registry.LocalMachine.CreateSubKey(path, writable: true)) { }
            var expected = new NssmServiceConfiguration
            {
                Name = name,
                Application = @"C:\Windows\System32\cmd.exe",
                AppDirectory = @"C:\Windows\System32",
                RotateBytes = 0x0000000200000001UL
            };
            Assert.Equal(0, NssmRegistry.create_parameters(expected, false));
            Assert.Equal(0, NssmRegistry.get_parameters(name, false, out var actual));
            Assert.Equal(expected.Application, actual.Application);
            Assert.Equal(expected.RotateBytes, actual.RotateBytes);
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, false);
        }
    }

    [Fact]
    public void get_exit_action_falls_back_to_default()
    {
        if (!RegistryMutationEnabled()) return;
        var name = "NssmTranslation_" + Guid.NewGuid().ToString("N");
        var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path + @"\Parameters\AppExit", true);
            key!.SetValue("", "Restart", RegistryValueKind.String);
            key.SetValue("5", "Ignore", RegistryValueKind.String);
            Assert.Equal(0, NssmRegistry.get_exit_action(name, 5, out var exact, out var exactDefault));
            Assert.Equal("Ignore", exact);
            Assert.False(exactDefault);
            Assert.Equal(0, NssmRegistry.get_exit_action(name, 6, out var fallback, out var fallbackDefault));
            Assert.Equal("Restart", fallback);
            Assert.True(fallbackDefault);
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, false);
        }
    }

    [Fact]
    public void set_and_get_hook_use_event_subkey()
    {
        if (!RegistryMutationEnabled()) return;
        var name = "NssmTranslation_" + Guid.NewGuid().ToString("N");
        var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
        try
        {
            using (Registry.LocalMachine.CreateSubKey(path, true)) { }
            Assert.Equal(0, NssmRegistry.set_hook(name, "Start", "Pre", @"%COMSPEC% /c exit 0"));
            Assert.Equal(0, NssmRegistry.get_hook(name, "Start", "Pre", 32768, out var command));
            Assert.Contains("cmd.exe", command, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, false);
        }
    }

    private static bool RegistryMutationEnabled() =>
        OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_REGISTRY_MUTATION_TESTS") == "1";

    private static void WithTemporaryKey(Action<RegistryKey> action)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = $@"Software\MyPowerTools\Tests\NssmTranslation\{Guid.NewGuid():N}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)!;
            action(key);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}

public sealed class NssmRegistryMutationTests
{
    [Fact]
    public void create_messages_registers_event_source()
    {
        if (Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_REGISTRY_MUTATION_TESTS") != "1") return;
        Assert.Equal(0, NssmRegistry.create_messages());
    }
}
