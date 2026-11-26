extern alias TransferMcp;

using System.Text.Json;
using ModelContextProtocol.Server;
using CloudMode = TransferMcp::MyPowerTools.TransferMcp.CloudMode;
using CloudProvider = TransferMcp::MyPowerTools.TransferMcp.CloudProvider;
using CredentialKind = TransferMcp::MyPowerTools.TransferMcp.CredentialKind;
using RelayRoute = TransferMcp::MyPowerTools.TransferMcp.RelayRoute;
using TransferAccess = TransferMcp::MyPowerTools.TransferMcp.TransferAccess;
using TransferTools = TransferMcp::MyPowerTools.TransferMcp.TransferTools;

namespace MyPowerTools.Tests;

public sealed class TransferMcpEnumSerializationTests
{
    [Fact]
    public void TransferMcp_enums_serialize_and_deserialize_exact_wire_tokens_and_reject_integers()
    {
        Assert.Equal("\"auto\"", JsonSerializer.Serialize(RelayRoute.Auto));
        Assert.Equal("\"quark\"", JsonSerializer.Serialize(RelayRoute.Quark));
        Assert.Equal(RelayRoute.Quark, JsonSerializer.Deserialize<RelayRoute>("\"quark\""));

        Assert.Equal("\"auto\"", JsonSerializer.Serialize(CloudMode.Auto));
        Assert.Equal("\"cloud-only\"", JsonSerializer.Serialize(CloudMode.CloudOnly));
        Assert.Equal("\"public-relay\"", JsonSerializer.Serialize(CloudMode.PublicRelay));
        Assert.Equal(CloudMode.CloudOnly, JsonSerializer.Deserialize<CloudMode>("\"cloud-only\""));

        Assert.Equal("\"quark\"", JsonSerializer.Serialize(CloudProvider.Quark));
        Assert.Equal("\"baidu\"", JsonSerializer.Serialize(CloudProvider.Baidu));
        Assert.Equal(CloudProvider.Baidu, JsonSerializer.Deserialize<CloudProvider>("\"baidu\""));

        Assert.Equal("\"cookie\"", JsonSerializer.Serialize(CredentialKind.Cookie));
        Assert.Equal("\"refreshToken\"", JsonSerializer.Serialize(CredentialKind.RefreshToken));
        Assert.Equal(CredentialKind.RefreshToken, JsonSerializer.Deserialize<CredentialKind>("\"refreshToken\""));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CloudMode>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CloudProvider>("0"));
    }

    [Fact]
    public void TransferMcp_tool_schemas_advertise_expected_string_enum_members()
    {
        var toolsInstance = new TransferTools(new TransferAccess());
        var selectTool = McpServerTool.Create(
            typeof(TransferTools).GetMethod(nameof(TransferTools.mpt_cloud_select))!,
            toolsInstance);
        var completeTool = McpServerTool.Create(
            typeof(TransferTools).GetMethod(nameof(TransferTools.mpt_cloud_complete))!,
            toolsInstance);

        var modeEnum = selectTool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("mode")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(["auto", "cloud-only", "public-relay"], modeEnum);

        var kindEnum = completeTool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("kind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(["cookie", "refreshToken"], kindEnum);
    }
}
