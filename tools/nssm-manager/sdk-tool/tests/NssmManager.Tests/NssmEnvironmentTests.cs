using NssmManager.Compatibility;

namespace NssmManager.Tests;

public sealed class NssmDoubleNullTests
{
    [Fact]
    public void format_double_null_matches_upstream_layout()
    {
        const string block = "one\0two\0\0";
        Assert.Equal(0, NssmDoubleNull.format_double_null(block, (uint)block.Length, out var formatted, out var length));
        Assert.Equal("one\r\ntwo\0\0", formatted);
        Assert.Equal((uint)formatted!.Length, length);
    }

    [Fact]
    public void unformat_double_null_strips_cr_and_blank_lines()
    {
        const string formatted = "\r\none\r\n\r\ntwo\n";
        Assert.Equal(0, NssmDoubleNull.unformat_double_null(formatted, (uint)formatted.Length, out var block, out var length));
        Assert.Equal("one\0two\0\0", block);
        Assert.Equal((uint)block!.Length, length);
    }

    [Fact]
    public void copy_double_null_honours_explicit_length()
    {
        const string block = "one\0two\0\0ignored";
        Assert.Equal(0, NssmDoubleNull.copy_double_null(block, 9, out var copy));
        Assert.Equal("one\0two\0\0", copy);
    }

    [Fact]
    public void append_to_double_null_replaces_matching_key()
    {
        const string block = "A=1\0B=2\0\0";
        Assert.Equal(0, NssmDoubleNull.append_to_double_null(block, (uint)block.Length, out var output, out var length, "a=3", 2, false));
        Assert.Equal(new[] { "a=3", "B=2" }, NssmDoubleNull.ToStrings(output, length));
    }

    [Fact]
    public void remove_from_double_null_matches_prefix_contract()
    {
        const string block = "Alpha\0Beta\0Alphabet\0\0";
        Assert.Equal(0, NssmDoubleNull.remove_from_double_null(block, (uint)block.Length, out var output, out var length, "Al", 2, true));
        Assert.Equal(new[] { "Beta" }, NssmDoubleNull.ToStrings(output, length));
    }
}

public sealed class NssmEnvironmentTests
{
    [Fact]
    public void environment_length_includes_double_nul() =>
        Assert.Equal((nuint)9, NssmEnvironment.environment_length("one\0two\0\0tail"));

    [Fact]
    public void copy_environment_block_uses_double_nul_length() =>
        Assert.Equal("one\0two\0\0", NssmEnvironment.copy_environment_block("one\0two\0\0tail"));

    [Fact]
    public void useful_environment_skips_drive_entries() =>
        Assert.Equal("A=1\0\0", NssmEnvironment.useful_environment("=C:=C:\\work\0=D:=D:\\tmp\0A=1\0\0"));

    [Fact]
    public void expand_environment_string_matches_windows_percent_syntax()
    {
        var name = $"NSSM_TRANSLATION_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "expanded");
        try
        {
            Assert.Equal("expanded/value", NssmEnvironment.expand_environment_string($"%{name}%/value"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void set_environment_block_expands_values_and_counts_failures()
    {
        var source = $"NSSM_TRANSLATION_SOURCE_{Guid.NewGuid():N}";
        var target = $"NSSM_TRANSLATION_TARGET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(source, "yes");
        try
        {
            var block = $"{target}=%{source}%\0\0";
            Assert.Equal(0, NssmEnvironment.set_environment_block(block));
            Assert.Equal("yes", Environment.GetEnvironmentVariable(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable(source, null);
            Environment.SetEnvironmentVariable(target, null);
        }
    }

    [Fact]
    public void unset_environment_block_removes_named_values()
    {
        var name = $"NSSM_TRANSLATION_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "value");
        Assert.Equal(0, NssmEnvironment.unset_environment_block($"{name}=ignored\0\0"));
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void test_environment_rejects_malformed_block()
    {
        Assert.Equal(1, NssmEnvironment.test_environment("A=1\0"));
        Assert.Equal(1, NssmEnvironment.test_environment("missing-equals\0\0"));
    }

    [Fact]
    public void copy_environment_is_double_nul_terminated()
    {
        var block = NssmEnvironment.copy_environment();
        Assert.NotNull(block);
        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
    }

    [Fact]
    public void append_to_environment_block_replaces_key_case_insensitively()
    {
        const string block = "Path=old\0Other=x\0\0";
        Assert.Equal(0, NssmEnvironment.append_to_environment_block(block, (uint)block.Length, "PATH=new", out var output, out var length));
        Assert.Equal(new[] { "PATH=new", "Other=x" }, NssmDoubleNull.ToStrings(output, length));
    }

    [Theory]
    [InlineData("PATH", new[] { "Other=x" })]
    [InlineData("PATH=old", new[] { "Other=x" })]
    [InlineData("PATH=different", new[] { "Path=old", "Other=x" })]
    public void remove_from_environment_block_honours_optional_value(string remove, string[] expected)
    {
        const string block = "Path=old\0Other=x\0\0";
        Assert.Equal(0, NssmEnvironment.remove_from_environment_block(block, (uint)block.Length, remove, out var output, out var length));
        Assert.Equal(expected, NssmDoubleNull.ToStrings(output, length));
    }
}
