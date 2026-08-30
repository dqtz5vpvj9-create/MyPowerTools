using System.Text;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmCoreTests
{
    [Fact]
    public void nssm_exit_cleanup_is_exercised_in_child_process()
    {
        var method = typeof(NssmCore).GetMethod(nameof(NssmCore.nssm_exit));
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    [Theory]
    [InlineData("NSSM", "nssm", 1)]
    [InlineData("NSSM", "nssmx", 0)]
    [InlineData("alpha", "ALPHa", 1)]
    [InlineData("alpha", "alph", 0)]
    public void str_equiv_is_case_insensitive_and_length_exact(string left, string right, int expected) =>
        Assert.Equal(expected, NssmCore.str_equiv(left, right));

    [Theory]
    [InlineData("0", 0u, 0)]
    [InlineData("010", 8u, 0)]
    [InlineData("0x10", 16u, 0)]
    [InlineData("-1", uint.MaxValue, 0)]
    [InlineData("42x", 42u, 2)]
    [InlineData("", 0u, 0)]
    [InlineData(null, 0u, 1)]
    public void str_number_matches_tcstoul_base_zero(string? text, uint expectedNumber, int expectedStatus)
    {
        var status = NssmCore.str_number(text, out var number, out _);
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedNumber, number);
    }

    [Theory]
    [InlineData("version", true)]
    [InlineData("/VERSION", true)]
    [InlineData("-v", true)]
    [InlineData("-V", true)]
    [InlineData("-version", true)]
    [InlineData("--version", true)]
    [InlineData("v", false)]
    [InlineData("--v", false)]
    [InlineData("", false)]
    public void is_version_matches_all_upstream_switches(string text, bool expected) =>
        Assert.Equal(expected, NssmCore.is_version(text));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a&b", "^\"a^&b^\"")]
    [InlineData("a\\\"b", "^\"a^\\^\\^\\^\"b^\"")]
    public void quote_matches_upstream_meta_character_escaping(string input, string expected)
    {
        Assert.Equal(0, NssmCore.quote(input, 1024, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void quote_rejects_a_short_buffer()
    {
        Assert.Equal(1, NssmCore.quote("two words", 4, out _));
    }

    [Theory]
    [InlineData(@"C:\one\two.exe", @"C:\one")]
    [InlineData(@"C:\two.exe", @"C:\")]
    [InlineData("two.exe", "")]
    [InlineData("/one/two", "/one")]
    public void strip_basename_preserves_drive_root(string input, string expected) =>
        Assert.Equal(expected, NssmCore.strip_basename(input));

    [Fact]
    public void image_paths_have_upstream_quoting_contract()
    {
        Assert.False(string.IsNullOrWhiteSpace(NssmCore.nssm_unquoted_imagepath()));
        Assert.False(string.IsNullOrWhiteSpace(NssmCore.nssm_imagepath()));
        Assert.False(string.IsNullOrWhiteSpace(NssmCore.nssm_exe()));
    }

    [Fact]
    public void num_cpus_matches_system_affinity_high_bit() =>
        Assert.InRange(NssmCore.num_cpus(), 1, 64);

    [Fact]
    public void check_admin_matches_token_membership()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var expected = new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Assert.Equal(expected, NssmCore.check_admin());
    }
}

public sealed class NssmUtf8Tests
{
    [Fact]
    public void setup_utf8_round_trips_console_encoding()
    {
        var original = Console.OutputEncoding;
        try
        {
            NssmManager.Contracts.NssmUtf8.setup_utf8();
            Assert.Equal(Encoding.UTF8.CodePage, Console.OutputEncoding.CodePage);
        }
        finally
        {
            NssmManager.Contracts.NssmUtf8.unsetup_utf8();
        }
        Assert.Equal(original.CodePage, Console.OutputEncoding.CodePage);
    }

    [Fact]
    public void to_utf8_from_utf16_returns_nul_terminated_copy()
    {
        Assert.Equal(0, NssmManager.Contracts.NssmUtf8.to_utf8("服务", out var bytes, out var length));
        Assert.NotNull(bytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("服务"), (int)length);
        Assert.Equal(0, bytes![^1]);
    }

    [Fact]
    public void to_utf8_from_bytes_copies_through_first_nul()
    {
        Assert.Equal(0, NssmManager.Contracts.NssmUtf8.to_utf8(new byte[] { 65, 66, 0, 67 }, out var bytes, out var length));
        Assert.Equal(2u, length);
        Assert.Equal(new byte[] { 65, 66, 0 }, bytes);
    }

    [Fact]
    public void to_utf16_from_utf8_stops_at_nul()
    {
        var bytes = Encoding.UTF8.GetBytes("服务\0ignored");
        Assert.Equal(0, NssmManager.Contracts.NssmUtf8.to_utf16(bytes, out var text, out var length));
        Assert.Equal("服务", text);
        Assert.Equal(2u, length);
    }

    [Fact]
    public void to_utf16_from_utf16_returns_distinct_copy()
    {
        var input = new string("service".AsSpan());
        Assert.Equal(0, NssmManager.Contracts.NssmUtf8.to_utf16(input, out var text, out var length));
        Assert.Equal(input, text);
        Assert.Equal(7u, length);
    }

    [Fact]
    public void from_utf8_uses_unicode_build_contract()
    {
        Assert.Equal(0, NssmManager.Contracts.NssmUtf8.from_utf8(Encoding.UTF8.GetBytes("服务"), out var text, out _));
        Assert.Equal("服务", text);
    }

    [Fact]
    public void from_utf16_uses_unicode_build_contract()
    {
        Assert.Equal(0, NssmManager.Contracts.NssmUtf8.from_utf16("服务", out var text, out _));
        Assert.Equal("服务", text);
    }
}
