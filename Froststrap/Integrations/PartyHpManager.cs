// SPDX-FileCopyrightText: 2026 Froststrap
//
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Froststrap.Integrations
{
    internal class PartyMemberHp
    {
        public string Username { get; set; } = string.Empty;
        public float CurrentHp { get; set; } = 100f;
        public float MaxHp { get; set; } = 100f;
        public int Percentage => MaxHp > 0 ? (int)((CurrentHp / MaxHp) * 100) : 0;
        public string AvatarUrl { get; set; } = string.Empty;
        public bool IsSelf => string.Equals(Username, Environment.UserName, StringComparison.OrdinalIgnoreCase);
        public string DisplayName => IsSelf ? $"{Username} (you)" : Username;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

    internal static class PartyHpManager
    {
        private static ClientWebSocket? _wsClient;
        private static CancellationTokenSource? _cts;
        public static ConcurrentDictionary<string, PartyMemberHp> PartyMembers { get; } = new();

        public static event EventHandler? PartyUpdated;

        public static bool IsConnected => _wsClient?.State == WebSocketState.Open;

        public static async Task StartAsync()
        {
            if (!App.Settings.Prop.EnablePartyHpSync)
                return;

            Stop();

            _cts = new CancellationTokenSource();
            _wsClient = new ClientWebSocket();

            string url = App.Settings.Prop.PartyWebSocketUrl;
            string room = App.Settings.Prop.PartyRoomCode;

            if (string.IsNullOrWhiteSpace(url))
                url = "ws://158.178.224.179:8000/ws";

            if (string.IsNullOrWhiteSpace(room))
                room = "default-party";

            string playerName = Environment.UserName;

            url = url.TrimEnd('/');
            Uri uri = new($"{url}/{Uri.EscapeDataString(room)}/{Uri.EscapeDataString(playerName)}");

            try
            {
                App.Logger.Debug($"[PartyHP] Connecting to WebSocket: {uri}");
                await _wsClient.ConnectAsync(uri, _cts.Token);
                App.Logger.Debug("[PartyHP] Connected successfully.");

                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
                _ = Task.Run(() => BroadcastLoopAsync(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                App.Logger.Error($"[PartyHP] Connection failed: {ex.Message}");
            }
        }

        public static void Stop()
        {
            try
            {
                _cts?.Cancel();
                if (_wsClient != null)
                {
                    if (_wsClient.State == WebSocketState.Open)
                    {
                        _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down", CancellationToken.None).Wait(1000);
                    }
                    _wsClient.Dispose();
                }
            }
            catch { }
            finally
            {
                _wsClient = null;
                _cts = null;
                PartyMembers.Clear();
                PartyUpdated?.Invoke(null, EventArgs.Empty);
            }
        }

        public static async Task SendLocalHpAsync(float currentHp, float maxHp)
        {
            if (!IsConnected || _wsClient == null)
                return;

            try
            {
                var payload = new
                {
                    type = "hp_update",
                    currentHp = currentHp,
                    maxHp = maxHp
                };

                string json = JsonSerializer.Serialize(payload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                await _wsClient.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                App.Logger.Error($"[PartyHP] Send error: {ex.Message}");
            }
        }

        private static async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[1024 * 4];
            while (!token.IsCancellationRequested && _wsClient?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    string type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";
                    string playerName = root.TryGetProperty("player", out var pProp) ? pProp.GetString() ?? "Unknown" : "Unknown";

                    if (type == "player_joined")
                    {
                        App.Logger.Debug($"[PartyHP] Player joined: {playerName}");
                    }
                    else if (type == "player_left")
                    {
                        PartyMembers.TryRemove(playerName, out _);
                        PartyUpdated?.Invoke(null, EventArgs.Empty);
                    }
                    else if (type == "hp_update" || root.TryGetProperty("currentHp", out _))
                    {
                        float cur = root.TryGetProperty("currentHp", out var cProp) ? cProp.GetSingle() : 100f;
                        float max = root.TryGetProperty("maxHp", out var mProp) ? mProp.GetSingle() : 100f;

                        if (!PartyMembers.TryGetValue(playerName, out var member))
                        {
                            member = new PartyMemberHp { Username = playerName };
                            PartyMembers[playerName] = member;
                        }

                        member.CurrentHp = cur;
                        member.MaxHp = max;
                        member.LastUpdated = DateTime.Now;

                        PartyUpdated?.Invoke(null, EventArgs.Empty);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    App.Logger.Error($"[PartyHP] Receive error: {ex.Message}");
                    await Task.Delay(2000, token);
                }
            }
        }

        private static async Task BroadcastLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    float simulatedHp = 97f;
                    float simulatedMaxHp = 100f;

                    string logPath = Path.Combine(Paths.DataRoot, "party_hp_local.json");
                    if (File.Exists(logPath))
                    {
                        string text = await File.ReadAllTextAsync(logPath, token);
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.TryGetProperty("currentHp", out var c)) simulatedHp = c.GetSingle();
                        if (doc.RootElement.TryGetProperty("maxHp", out var m)) simulatedMaxHp = m.GetSingle();
                    }

                    // Update self in local dict for immediate rendering
                    string selfName = Environment.UserName;
                    if (!PartyMembers.TryGetValue(selfName, out var selfMember))
                    {
                        selfMember = new PartyMemberHp { Username = selfName };
                        PartyMembers[selfName] = selfMember;
                    }
                    selfMember.CurrentHp = simulatedHp;
                    selfMember.MaxHp = simulatedMaxHp;
                    PartyUpdated?.Invoke(null, EventArgs.Empty);

                    await SendLocalHpAsync(simulatedHp, simulatedMaxHp);
                }
                catch { }

                await Task.Delay(1000, token);
            }
        }
    }
}
