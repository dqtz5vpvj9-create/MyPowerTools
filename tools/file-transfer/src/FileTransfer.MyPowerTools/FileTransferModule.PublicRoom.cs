using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;

namespace FileTransfer.MyPowerTools;

public sealed partial class FileTransferModule
{
    private readonly SemaphoreSlim _publicEnrollmentGate = new(1, 1);
    private string _publicRoomError = "";
    private bool PublicRoom => _linked == "public";
    private bool PublicPending => _linked is "public-pending" or "public-denied";

    private async Task EnsurePublicAuthorizationAsync(CancellationToken token)
    {
        if (_linked != "public-pending") return;
        await _publicEnrollmentGate.WaitAsync(token);
        try
        {
            if (_linked != "public-pending") return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await http.PostAsJsonAsync(new Uri(PublicRelayClient.BaseAddress,
                "/mpt/relay/v1/public-room/authorizations"), new { deviceId = Setting("deviceId") }, token);
            if (!response.IsSuccessStatusCode)
            {
                _publicRoomError = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "服务器暂未开放公屏授权，请稍后重试。" : $"无法取得公屏授权（HTTP {(int)response.StatusCode}），请重试。";
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _linked = "public-denied";
                    await _secrets.SaveAsync(Id, "conversation-linked", _linked, token);
                }
                throw new IOException(_publicRoomError);
            }
            var grant = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject();
            var room = grant["conversationId"]!.GetValue<string>();
            var credential = grant["token"]!.GetValue<string>();
            // Validate the returned routing identity before replacing the persisted one.
            using var check = new PublicRelayClient(room, credential);
            await _secrets.SaveAsync(Id, "conversation-key", credential, token);
            await _secrets.SaveAsync(Id, "conversation-id", room, token);
            await _secrets.SaveAsync(Id, "conversation-linked", "public", token);
            _conversationId = room; _conversationKey = credential; _linked = "public";
            _publicRoomError = "";
            ResetPublicRelay();
            _assistant = await _assistantStore!.ConfigureAsync(Identity(), token);
            _relayRevision = -1;
            EmitAssistantChanged("public.authorized");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (!token.IsCancellationRequested && _publicRoomError.Length == 0)
                _publicRoomError = "暂时无法连接服务器，请检查网络后重试。";
            EmitAssistantChanged("public.authorization-failed");
            throw;
        }
        finally { _publicEnrollmentGate.Release(); }
    }

    private async Task<object> PublicRoomActionAsync(string action, CancellationToken token)
    {
        await _relayGate.WaitAsync(token);
        try
        {
            if (action == "join")
            {
                if (PublicRoom && _relayAuthError.Length == 0) return new { joined = true };
                if (!PublicRoom && !PublicPending && _linked != "public-left")
                {
                    await _secrets.SaveAsync(Id, "private-conversation-id", _conversationId, token);
                    await _secrets.SaveAsync(Id, "private-conversation-key", _conversationKey, token);
                    await _secrets.SaveAsync(Id, "private-conversation-linked", _linked, token);
                }
                _linked = "public-pending"; _publicRoomError = "";
                await _secrets.SaveAsync(Id, "conversation-linked", _linked, token);
                await EnsurePublicAuthorizationAsync(token);
            }
            else if (action == "private")
            {
                var room = await SecretAsync("private-conversation-id", token);
                var key = await SecretAsync("private-conversation-key", token);
                if (string.IsNullOrEmpty(room) || string.IsNullOrEmpty(key)) throw new InvalidOperationException("没有原私人共享会话。");
                _conversationId = room; _conversationKey = key;
                _linked = await SecretAsync("private-conversation-linked", token) ?? "";
                await _secrets.SaveAsync(Id, "conversation-id", room, token);
                await _secrets.SaveAsync(Id, "conversation-key", key, token);
                await _secrets.SaveAsync(Id, "conversation-linked", _linked, token);
                _assistant = await _assistantStore!.ConfigureAsync(Identity(), token);
            }
            else
            {
                _linked = "public-left";
                await _secrets.SaveAsync(Id, "conversation-linked", _linked, token);
            }
            ResetPublicRelay(); _relayRevision = -1;
            EmitAssistantChanged("public." + action); SignalAssistant();
            return new { joined = PublicRoom };
        }
        finally { _relayGate.Release(); }
    }
}
