using Microsoft.Win32;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Windows;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NssmManager.Tests;

public sealed class NssmSettingsTranslationTests
{
    [Fact]
    public void defaults_and_types_match_upstream()
    {
        Assert.Equal(1, NssmSettingsTranslation.is_default("Default"));
        Assert.Equal(1, NssmSettingsTranslation.is_default("*"));
        Assert.Equal(1, NssmSettingsTranslation.is_default(""));
        Assert.Equal(0, NssmSettingsTranslation.is_default("All"));
        Assert.True(NssmSettingsTranslation.is_string_type(NssmSettingsTranslation.RegMultiSz));
        Assert.True(NssmSettingsTranslation.is_string_type(NssmSettingsTranslation.RegExpandSz));
        Assert.True(NssmSettingsTranslation.is_string_type(NssmSettingsTranslation.RegSz));
        Assert.False(NssmSettingsTranslation.is_string_type(NssmSettingsTranslation.RegDword));
        Assert.True(NssmSettingsTranslation.is_numeric_type(NssmSettingsTranslation.RegDword));
    }

    [Fact]
    public void value_from_string_matches_union_contract()
    {
        var value = new NssmSettingValue();
        Assert.Equal(0, NssmSettingsTranslation.value_from_string("x", value, ""));
        Assert.Null(value.String);
        Assert.Equal(1, NssmSettingsTranslation.value_from_string("x", value, "abc"));
        Assert.Equal("abc", value.String);
    }

    [Fact]
    public void number_and_string_settings_match_registry_contract()
    {
        WithKey(key =>
        {
            Assert.Equal(0, NssmSettingsTranslation.setting_set_number("svc", key, "Number", 10u, NssmSettingValue.FromString("10"), null));
            Assert.DoesNotContain("Number", key.GetValueNames());
            Assert.Equal(1, NssmSettingsTranslation.setting_set_number("svc", key, "Number", 10u, NssmSettingValue.FromString("11"), null));
            Assert.Equal(11, key.GetValue("Number"));
            var number = new NssmSettingValue();
            Assert.Equal(1, NssmSettingsTranslation.setting_get_number("svc", key, "Number", 10u, number, null));
            Assert.Equal(11u, number.Numeric);
            var missing = new NssmSettingValue();
            var descriptor = Assert.Single(NssmSettingsTranslation.Settings, item => item.Name == "AppStdinShareMode");
            Assert.Equal(0, NssmSettingsTranslation.get_setting("svc", key, descriptor, missing, null));
            Assert.Equal(2u, missing.Numeric);

            Assert.Equal(1, NssmSettingsTranslation.setting_set_string("svc", key, "Text", "", NssmSettingValue.FromString("%TEMP%"), null));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Text"));
            var text = new NssmSettingValue();
            Assert.Equal(1, NssmSettingsTranslation.setting_get_string("svc", key, "Text", "", text, null));
            Assert.Equal("%TEMP%", text.String);
            Assert.Equal(1, NssmSettingsTranslation.setting_set_string("svc", key, "Text", "", null, null));
            Assert.Equal(string.Empty, key.GetValue("Text", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        });
    }

    [Fact]
    public void dump_string_matches_upstream_command_shape()
    {
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, NssmSettingsTranslation.setting_dump_string("service name", NssmSettingsTranslation.RegSz, "AppParameters", NssmSettingValue.FromString("a&b"), null));
        }
        finally { Console.SetOut(previous); }
        Assert.Contains(" set \"service name\" AppParameters ^\"a^&b^\"", output.ToString());
    }

    [Fact]
    public void exit_action_and_hook_validation_match_upstream()
    {
        Assert.True(NssmSettingsTranslation.split_hook_name("Start/Pre", out var hookEvent, out var hookAction));
        Assert.Equal("Start", hookEvent);
        Assert.Equal("Pre", hookAction);
        Assert.False(NssmSettingsTranslation.split_hook_name("Stop/Post", out _, out _));
        Assert.False(NssmSettingsTranslation.split_hook_name("Exit/Pre", out _, out _));
        Assert.False(NssmSettingsTranslation.split_hook_name("missing", out _, out _));
    }

    [Fact]
    public void affinity_priority_and_environment_match_upstream()
    {
        WithKey(key =>
        {
            Assert.Equal(1, NssmSettingsTranslation.setting_set_affinity("svc", key, "AppAffinity", null, NssmSettingValue.FromString("0-2,4"), null));
            var affinity = new NssmSettingValue();
            Assert.Equal(1, NssmSettingsTranslation.setting_get_affinity("svc", key, "AppAffinity", null, affinity, null));
            Assert.Equal("0-2,4", affinity.String);

            Assert.Equal(0, NssmSettingsTranslation.setting_set_priority("svc", key, "AppPriority", "NORMAL_PRIORITY_CLASS", NssmSettingValue.FromString("NORMAL_PRIORITY_CLASS"), null));
            Assert.Equal(1, NssmSettingsTranslation.setting_set_priority("svc", key, "AppPriority", "NORMAL_PRIORITY_CLASS", NssmSettingValue.FromString("HIGH_PRIORITY_CLASS"), null));
            Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("AppPriority"));
            var priority = new NssmSettingValue();
            Assert.Equal(1, NssmSettingsTranslation.setting_get_priority("svc", key, "AppPriority", "NORMAL_PRIORITY_CLASS", priority, null));
            Assert.Equal("HIGH_PRIORITY_CLASS", priority.String);

            Assert.Equal(1, NssmSettingsTranslation.setting_set_environment("svc", key, "AppEnvironment", null, NssmSettingValue.FromString(":A=1\r\nB=2"), null));
            Assert.Equal(new[] { "A=1", "B=2" }, (string[])key.GetValue("AppEnvironment")!);
            Assert.Equal(1, NssmSettingsTranslation.setting_set_environment("svc", key, "AppEnvironment", null, NssmSettingValue.FromString("+A=3"), null));
            Assert.Equal(new[] { "A=3", "B=2" }, (string[])key.GetValue("AppEnvironment")!);
            var environment = new NssmSettingValue();
            Assert.Equal(1, NssmSettingsTranslation.setting_get_environment("svc", key, "AppEnvironment", null, environment, "a"));
            Assert.Equal("3", environment.String);
        });
    }

    [Fact]
    public void dependency_protocol_matches_upstream()
    {
        var value = NssmSettingValue.FromString(":Alpha\r\n+Beta");
        Assert.Equal(0, NssmSettingsTranslation.native_set_dependon("svc", IntPtr.Zero, out var block, out var length, value, NssmSettingsTranslation.DependencyGroups));
        Assert.Equal(new[] { "+Alpha", "+Beta" }, NssmDoubleNull.ToStrings(block, length));
    }

    [Fact]
    public void native_settings_validate_null_handles()
    {
        var value = new NssmSettingValue();
        Assert.Equal(-1, NssmSettingsTranslation.native_get_description("svc", IntPtr.Zero, "Description", null, value, null));
        Assert.Equal(-1, NssmSettingsTranslation.native_set_imagepath("svc", IntPtr.Zero, "ImagePath", null, NssmSettingValue.FromString("x"), null));
        Assert.Equal(-1, NssmSettingsTranslation.native_get_type("svc", IntPtr.Zero, "Type", null, value, null));
        Assert.Equal(-1, NssmSettingsTranslation.setting_dump_dependon("svc", IntPtr.Zero, "DependOnService", NssmSettingsTranslation.DependencyServices, value));
    }

    [Fact]
    public void change_service_config2_uses_the_unicode_entrypoint()
    {
        var nativeMethods = typeof(NssmSettingsTranslation).Assembly.GetType("NssmManager.Windows.NativeMethods", throwOnError: true)!;
        var method = nativeMethods.GetMethod("ChangeServiceConfig2", BindingFlags.Static | BindingFlags.NonPublic)!;
        var import = method.GetCustomAttribute<DllImportAttribute>()!;
        Assert.Equal("ChangeServiceConfig2W", import.EntryPoint);
        Assert.Equal(CharSet.Unicode, import.CharSet);
    }

    [Fact]
    public void dispatch_table_matches_upstream_order_and_defaults()
    {
        Assert.Equal(49, NssmSettingsTranslation.Settings.Count);
        Assert.Equal("Application", NssmSettingsTranslation.Settings[0].Name);
        Assert.Equal("Type", NssmSettingsTranslation.Settings[^1].Name);
        Assert.Equal(39, NssmSettingsTranslation.Settings.Count(item => !item.Native));
        Assert.Equal(10, NssmSettingsTranslation.Settings.Count(item => item.Native));
        Assert.Equal("NORMAL_PRIORITY_CLASS", NssmSettingsTranslation.Find("apppriority")!.DefaultValue);
        Assert.Equal(0u, NssmSettingsTranslation.Find("AppRotateDelay")!.DefaultValue);
        Assert.Equal(NssmSettingsTranslation.AdditionalMandatory, NssmSettingsTranslation.Find("AppExit")!.Additional);
    }

    private static void WithKey(Action<RegistryKey> action)
    {
        var relative = $@"Software\MyPowerTools\NssmManagerTests\{Guid.NewGuid():N}";
        using var key = Registry.CurrentUser.CreateSubKey(relative, writable: true)!;
        try { action(key); }
        finally { Registry.CurrentUser.DeleteSubKeyTree(relative, throwOnMissingSubKey: false); }
    }
}
