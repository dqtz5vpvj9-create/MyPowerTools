using System.Text;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Android.Tests;

/// <summary>
/// The connection-code rules the scanner and the Shell share. The important assertions are the
/// negative ones: an opaque base64url payload decodes to readable bytes, so anything that keeps a
/// prefix of it — including a "just the first few characters" diagnostic — is a credential leak.
/// </summary>
public sealed class MobileQrPayloadTests
{
    // A realistic receiver secret: FileTransfer.Core.Pairing.Encode puts it in the JSON payload.
    private const string Token = "s3cret-receiver-token-0123456789";
    private const string Address = "100.64.0.5";
    private const string DeviceName = "工作电脑";

    private static string PairCode { get; } = Encode(MobileQrPayload.PairPrefix,
        $$"""{"DeviceId":"dev-1","Name":"{{DeviceName}}","Address":"{{Address}}","Token":"{{Token}}"}""");

    // The contract's control code: endpoint, grant id, device name and grant token.
    private static string ControlCode { get; } = Encode(MobileQrPayload.ControlPrefix,
        $$"""{"version":1,"endpoint":"http://100.64.0.2:49541","grantId":"grant-7","deviceName":"{{DeviceName}}","token":"{{Token}}"}""");

    private static string CloudCode { get; } = Encode(MobileQrPayload.CloudPrefix,
        $$"""{"url":"https://dav.example/remote.php","username":"chris","password":"{{Token}}"}""");

    // "connect my devices": a conversation id and a join credential.
    private static string AssistantCode { get; } = Encode(MobileQrPayload.AssistantPrefix,
        $$"""{"version":1,"conversationId":"conv-3","deviceName":"{{DeviceName}}","token":"{{Token}}"}""");

    private static string Encode(string prefix, string json) =>
        prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A slice of the encoded payload from the middle of the JSON, which is where the secret sits.
    /// Base64 works on 3-byte groups, so the encoded text of the secret alone is not a substring of
    /// the encoded JSON; a real payload slice is what a "keep the first N characters" diagnostic
    /// would have emitted.
    /// </summary>
    private static string EncodedFragment(string code, int characters = 24)
    {
        var payload = code[(code.IndexOf('/', "mpt://".Length) + 1)..];
        var start = Math.Max(0, (payload.Length / 2) - (characters / 2));
        return payload.Substring(start, Math.Min(characters, payload.Length - start));
    }

    private static string CodeFor(MobileQrCodeKind kind) => kind switch
    {
        MobileQrCodeKind.Pairing => PairCode,
        MobileQrCodeKind.CloudRelay => CloudCode,
        MobileQrCodeKind.RemoteControl => ControlCode,
        _ => AssistantCode
    };

    [Theory]
    [InlineData(MobileQrCodeKind.Pairing)]
    [InlineData(MobileQrCodeKind.CloudRelay)]
    [InlineData(MobileQrCodeKind.RemoteControl)]
    [InlineData(MobileQrCodeKind.Assistant)]
    public void Each_internal_link_family_is_classified_and_kept_whole(MobileQrCodeKind expected)
    {
        var code = CodeFor(expected);

        Assert.True(MobileQrPayload.IsRecognised(code));
        Assert.True(MobileQrPayload.TryClassify(code, out var classified, out var reason));

        Assert.Null(reason);
        Assert.NotNull(classified);
        Assert.Equal(expected, classified!.Kind);
        // The owning module command re-validates and stores the link, so it needs the exact string.
        Assert.Equal(code, classified.Value);
        // Every family carries a credential: a receiver secret, a relay password or a grant token.
        Assert.True(classified.CarriesCredential);
    }

    [Theory]
    [InlineData(MobileQrCodeKind.Pairing, "设备连接码")]
    [InlineData(MobileQrCodeKind.CloudRelay, "网盘连接码")]
    [InlineData(MobileQrCodeKind.RemoteControl, "远程工具连接码")]
    [InlineData(MobileQrCodeKind.Assistant, "文件助手连接码")]
    public void A_log_line_contains_the_kind_and_the_length_and_no_payload_character(
        MobileQrCodeKind kind, string label)
    {
        var code = CodeFor(kind);
        var payload = code[(code.IndexOf('/', "mpt://".Length) + 1)..];

        Assert.True(MobileQrPayload.TryClassify(code, out var classified, out _));
        var description = classified!.Describe();

        Assert.Contains(label, description, StringComparison.Ordinal);
        Assert.Contains(payload.Length.ToString(), description, StringComparison.Ordinal);

        // Plaintext must not appear.
        Assert.DoesNotContain(Token, description, StringComparison.Ordinal);
        Assert.DoesNotContain(Address, description, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceName, description, StringComparison.Ordinal);

        // ... and neither may any encoded slice of the payload. This is the regression the previous
        // "first 48 characters" diagnostic failed: the payload is base64url of JSON, so a kept prefix
        // decodes straight back to readable fields (device id, address, name, secret).
        Assert.DoesNotContain(EncodedFragment(code), description, StringComparison.Ordinal);
        Assert.DoesNotContain(payload[..Math.Min(48, payload.Length)], description, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Token))[..8], description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_description_is_exactly_kind_plus_length()
    {
        Assert.True(MobileQrPayload.TryClassify(PairCode, out var classified, out _));
        var payloadLength = PairCode.Length - MobileQrPayload.PairPrefix.Length;

        Assert.Equal($"设备连接码 · 内容已隐藏（长度 {payloadLength}）", classified!.Describe());
    }

    [Theory]
    [InlineData(MobileQrCodeKind.Pairing)]
    [InlineData(MobileQrCodeKind.CloudRelay)]
    [InlineData(MobileQrCodeKind.RemoteControl)]
    [InlineData(MobileQrCodeKind.Assistant)]
    public void ToString_and_the_result_text_are_redacted_too(MobileQrCodeKind kind)
    {
        // A record reaches a log or an exception message by accident, and the compiler-generated text
        // would print the credential. Neither the code nor the result may do that.
        Assert.True(MobileQrPayload.TryClassify(CodeFor(kind), out var code, out _));
        var text = code!.ToString();

        Assert.Equal(code.Describe(), text);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("mpt://", text, StringComparison.Ordinal);

        var success = MobileQrScanResult.Success(code);
        Assert.DoesNotContain(Token, success.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("mpt://", success.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_link_is_not_echoed_into_diagnostics()
    {
        var description = MobileQrPayload.Describe(
            MobileQrCodeKind.Unsupported, "https://example.com/secret?token=" + Token);

        Assert.DoesNotContain(Token, description, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com")]
    [InlineData("842916")]
    [InlineData("mpt://pair/")]
    [InlineData("mpt://control/")]
    [InlineData("mpt://pair/not base64!")]
    [InlineData("mpt://pair/a=b")]
    [InlineData("mpt://other/abc")]
    [InlineData("mpt://PAIR/abc")]
    public void Anything_that_is_not_an_internal_link_is_rejected_with_a_reason(string? value)
    {
        Assert.False(MobileQrPayload.TryClassify(value, out var code, out var reason));
        Assert.Null(code);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void An_oversized_string_is_rejected_before_it_reaches_a_module_decoder()
    {
        var value = MobileQrPayload.PairPrefix + new string('A', 5000);
        Assert.False(MobileQrPayload.IsRecognised(value));
        Assert.False(MobileQrPayload.TryClassify(value, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void A_failed_scan_result_can_never_carry_a_code()
    {
        var cancelled = MobileQrScanResult.Cancelled("已取消扫描。");
        Assert.False(cancelled.Succeeded);
        Assert.Null(cancelled.Code);
        Assert.Equal("已取消扫描。", cancelled.Message);

        var denied = MobileQrScanResult.Cancelled("需要相机权限。", openManualEntry: true, error: MobileQrScanError.PermissionDenied);
        Assert.Equal(MobileQrScanError.PermissionDenied, denied.Error);
        Assert.True(denied.OpenManualEntry);

        var failed = MobileQrScanResult.Failed("无法打开相机。");
        Assert.Equal(MobileQrScanError.Unavailable, failed.Error);
        Assert.False(failed.Succeeded);

        Assert.True(MobileQrScanResult.Success(ClassifiedPair()).Succeeded);
    }

    private static MobileQrConnectionCode ClassifiedPair()
    {
        Assert.True(MobileQrPayload.TryClassify(PairCode, out var code, out _));
        return code!;
    }
}
