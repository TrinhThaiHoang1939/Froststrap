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
        public long UserId { get; set; }
        public float CurrentHp { get; set; } = 100f;
        public float MaxHp { get; set; } = 100f;
        public int Percentage => MaxHp > 0 ? (int)((CurrentHp / MaxHp) * 100) : 0;
        public string AvatarUrl { get; set; } = string.Empty;
        public bool IsSelf => string.Equals(Username, AccountManager.AccountManager.Shared.ActiveAccount?.Username, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Username, Environment.UserName, StringComparison.OrdinalIgnoreCase);
        public string DisplayName => IsSelf ? $"{Username} (you)" : Username;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

    internal static class PartyHpManager
    {
        private const int MaxReconnectDelayMs = 30_000;
        private const int InitialReconnectDelayMs = 1_000;
        private const int MaxFrameBytes = 64 * 1024;

        private static ClientWebSocket? _wsClient;
        private static CancellationTokenSource? _cts;
        private static readonly SemaphoreSlim _sendLock = new(1, 1);
        private static readonly object _lifecycleLock = new();
        private static Task? _supervisorTask;
        private static string? _cachedPlayerName;
        private static long _cachedPlayerUserId;
        private static volatile bool _fatalClose;
        public static ConcurrentDictionary<string, PartyMemberHp> PartyMembers { get; } = new();

        public static event EventHandler? PartyUpdated;
        public static event EventHandler<string>? ConnectionFailed;

        public static bool IsConnected => _wsClient?.State == WebSocketState.Open;

        private static async Task<long> TryGetUserIdFromRobloxLogsAsync()
        {
            try
            {
                string logsDir = Paths.RobloxLogs;
                if (!Directory.Exists(logsDir)) return 0;

                var latestLog = new DirectoryInfo(logsDir)
                    .GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestLog == null) return 0;

                string text;
                using (var fs = new FileStream(latestLog.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    text = await sr.ReadToEndAsync();
                }

                var match = System.Text.RegularExpressions.Regex.Match(text, @"userid:([0-9]+)");
                if (match.Success && long.TryParse(match.Groups[1].Value, out long userId))
                {
                    return userId;
                }
            }
            catch (Exception ex)
            {
                App.Logger.Debug($"[PartyHP] Error reading Roblox logs for userId: {ex.Message}");
            }
            return 0;
        }

        private static async Task<(string username, long userId)> FetchUsernameFromUserIdAsync(long userId)
        {
            try
            {
                string url = $"https://users.roblox.com/v1/users/{userId}";
                string json = await App.HttpClient.GetStringAsync(new Uri(url));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                {
                    string username = nameProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(username))
                    {
                        return (username, userId);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.Debug($"[PartyHP] Failed to fetch username for userId {userId}: {ex.Message}");
            }
            return ("", userId);
        }

        private static async Task<(string username, long userId)> GetCurrentRobloxUserAsync()
        {
            try
            {
                // 1. Check AccountManager active or saved accounts
                var activeAcc = AccountManager.AccountManager.Shared.ActiveAccount ?? (AccountManager.AccountManager.Shared.Accounts.Count > 0 ? AccountManager.AccountManager.Shared.Accounts[0] : null);
                if (activeAcc != null && activeAcc.UserId > 0 && !string.IsNullOrEmpty(activeAcc.Username))
                {
                    return (activeAcc.Username, activeAcc.UserId);
                }

                // 2. Automatically load Roblox cookies & query authenticated user API
                var cookiesMgr = new CookiesManager();
                await cookiesMgr.LoadCookies();
                var authUser = await cookiesMgr.GetAuthenticated();
                if (authUser != null && authUser.Id > 0 && !string.IsNullOrEmpty(authUser.Username))
                {
                    return (authUser.Username, authUser.Id);
                }

                // 3. Fallback to active Roblox client logs
                long logUserId = await TryGetUserIdFromRobloxLogsAsync();
                if (logUserId > 0)
                {
                    var (username, uid) = await FetchUsernameFromUserIdAsync(logUserId);
                    if (!string.IsNullOrEmpty(username))
                    {
                        return (username, uid);
                    }
                }

                // 4. Last resort: custom settings or environment username
                if (!string.IsNullOrEmpty(App.Settings.Prop.PartyUsername))
                {
                    return (App.Settings.Prop.PartyUsername, App.Settings.Prop.PartyUserId);
                }
            }
            catch (Exception ex)
            {
                App.Logger.Error($"[PartyHP] Failed to resolve Roblox user: {ex.Message}");
            }

            return (Environment.UserName, 0);
        }

        public static Task StartAsync()
        {
            if (!App.Settings.Prop.EnablePartyHpSync)
                return Task.CompletedTask;

            lock (_lifecycleLock)
            {
                StopInternal();
                _fatalClose = false;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _supervisorTask = Task.Run(() => SupervisorLoopAsync(token), token);
            }
            PartyUpdated?.Invoke(null, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public static void Stop()
        {
            lock (_lifecycleLock)
            {
                StopInternal();
            }
            PartyUpdated?.Invoke(null, EventArgs.Empty);
        }

        private static void StopInternal()
        {
            try { _cts?.Cancel(); } catch { }

            var ws = _wsClient;
            _wsClient = null;
            if (ws != null)
            {
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
            }

            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _cachedPlayerName = null;
            _cachedPlayerUserId = 0;

            // Drop stale members so a reconnect / team switch starts from a clean slate.
            PartyMembers.Clear();
        }

        private static async Task SupervisorLoopAsync(CancellationToken token)
        {
            int delayMs = InitialReconnectDelayMs;

            while (!token.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                    string url = App.Settings.Prop.PartyWebSocketUrl;
                    string room = App.Settings.Prop.PartyRoomCode;

                    if (string.IsNullOrWhiteSpace(url))
                        url = "ws://158.178.224.179:8000/ws";
                    if (string.IsNullOrWhiteSpace(room))
                        room = "default-party";

                    if (_cachedPlayerName == null)
                    {
                        var (name, uid) = await GetCurrentRobloxUserAsync();
                        _cachedPlayerName = name;
                        _cachedPlayerUserId = uid;
                    }
                    string playerName = _cachedPlayerName!;
                    long myUserId = _cachedPlayerUserId;

                    int nameBytes = Encoding.UTF8.GetByteCount(playerName);
                    if (nameBytes > 40)
                    {
                        App.Logger.Error($"[PartyHP] Player name '{playerName}' exceeds server max ({nameBytes} > 40 bytes). Aborting.");
                        _fatalClose = true;
                        ConnectionFailed?.Invoke(null, "player_name_too_long");
                        return;
                    }

                    url = url.TrimEnd('/');
                    Uri uri = new($"{url}/{Uri.EscapeDataString(room)}/{Uri.EscapeDataString(playerName)}");

                    App.Logger.Debug($"[PartyHP] Connecting to WebSocket: {uri}");
                    await ws.ConnectAsync(uri, token);
                    App.Logger.Debug("[PartyHP] Connected successfully.");

                    _wsClient = ws;
                    delayMs = InitialReconnectDelayMs;

                    if (myUserId > 0)
                    {
                        string? myAvatar = await GetPlayerAvatarUrlAsync(myUserId);
                        var selfMember = PartyMembers.GetOrAdd(playerName,
                            _ => new PartyMemberHp { Username = playerName, UserId = myUserId });
                        selfMember.UserId = myUserId;
                        if (!string.IsNullOrEmpty(myAvatar))
                            selfMember.AvatarUrl = myAvatar;
                    }

                    var receive = ReceiveLoopAsync(ws, token);
                    var broadcast = BroadcastLoopAsync(ws, token);
                    await Task.WhenAny(receive, broadcast);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    App.Logger.Error($"[PartyHP] Session error: {ex.Message}");
                    ConnectionFailed?.Invoke(null, ex.Message);
                }
                finally
                {
                    if (ws != null)
                    {
                        try { ws.Abort(); } catch { }
                        try { ws.Dispose(); } catch { }
                    }
                    if (ReferenceEquals(_wsClient, ws))
                        _wsClient = null;
                }

                if (token.IsCancellationRequested)
                    return;

                if (_fatalClose)
                {
                    App.Logger.Info("[PartyHP] Fatal server close received; not retrying.");
                    return;
                }

                try { await Task.Delay(delayMs, token); }
                catch (OperationCanceledException) { return; }
                delayMs = Math.Min(delayMs * 2, MaxReconnectDelayMs);
            }
        }

        public static Task SendLocalHpAsync(float currentHp, float maxHp)
        {
            var ws = _wsClient;
            var token = _cts?.Token ?? CancellationToken.None;
            return SendInternalAsync(ws, currentHp, maxHp, token);
        }

        private static async Task SendInternalAsync(ClientWebSocket? ws, float currentHp, float maxHp, CancellationToken token)
        {
            if (ws == null || ws.State != WebSocketState.Open)
                return;

            long myUserId = _cachedPlayerUserId > 0
                ? _cachedPlayerUserId
                : AccountManager.AccountManager.Shared.ActiveAccount?.UserId ?? 0;

            var payload = new
            {
                type = "hp_update",
                currentHp,
                maxHp,
                userId = myUserId
            };

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (ws.State != WebSocketState.Open)
                    return;
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (WebSocketException ex)
            {
                App.Logger.Error($"[PartyHP] Send error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public static async Task<string?> GetPlayerAvatarUrlAsync(long userId)
        {
            if (userId <= 0) return null;
            try
            {
                string apiUrl = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
                string response = await App.HttpClient.GetStringAsync(new Uri(apiUrl));

                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
                {
                    var item = dataArray[0];
                    if (item.TryGetProperty("imageUrl", out var imgProp))
                    {
                        return imgProp.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.Error($"[PartyHP] Failed to fetch avatar for userId {userId}: {ex.Message}");
            }
            return null;
        }

        private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[4096];
            using var frame = new MemoryStream();

            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                frame.SetLength(0);

                WebSocketReceiveResult result;
                try
                {
                    while (true)
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            App.Logger.Debug($"[PartyHP] Server closed: {result.CloseStatus} '{result.CloseStatusDescription}'");
                            if (result.CloseStatus == (WebSocketCloseStatus)1008)
                            {
                                // Policy violation: name taken / party full / name too long. No point retrying.
                                _fatalClose = true;
                                ConnectionFailed?.Invoke(null, result.CloseStatusDescription ?? "party_full_or_name_taken");
                            }
                            return;
                        }

                        if (frame.Length + result.Count > MaxFrameBytes)
                        {
                            App.Logger.Error("[PartyHP] Incoming frame exceeded limit, dropping.");
                            // Drain the rest of this message before resuming.
                            while (!result.EndOfMessage)
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            frame.SetLength(0);
                            break;
                        }

                        frame.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage) break;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (WebSocketException ex)
                {
                    App.Logger.Error($"[PartyHP] Receive error: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    App.Logger.Error($"[PartyHP] Unexpected receive error: {ex.Message}");
                    return;
                }

                if (frame.Length == 0 || result.MessageType != WebSocketMessageType.Text)
                    continue;

                try
                {
                    await HandleIncomingMessageAsync(frame.GetBuffer().AsMemory(0, (int)frame.Length)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    App.Logger.Debug($"[PartyHP] Bad message dropped: {ex.Message}");
                }
            }
        }

        private static async Task HandleIncomingMessageAsync(ReadOnlyMemory<byte> payload)
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "hp_update" : "hp_update";

            // Server-originated messages that don't carry a player field.
            if (type == "server_warning")
            {
                string warning = root.TryGetProperty("reason", out var rProp) ? rProp.GetString() ?? "" : "";
                App.Logger.Warn($"[PartyHP] Server warning: {warning}");
                return;
            }

            string playerName = root.TryGetProperty("player", out var pProp) ? pProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(playerName) && root.TryGetProperty("username", out var uProp))
                playerName = uProp.GetString() ?? "";

            if (string.IsNullOrEmpty(playerName))
                return;

            long userId = 0;
            if (root.TryGetProperty("userId", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                && idProp.TryGetInt64(out long uid))
                userId = uid;

            if (type == "player_joined")
            {
                App.Logger.Debug($"[PartyHP] Player joined: {playerName}");
                PartyMembers.GetOrAdd(playerName, _ => new PartyMemberHp { Username = playerName });
                PartyUpdated?.Invoke(null, EventArgs.Empty);
            }
            else if (type == "player_left")
            {
                PartyMembers.TryRemove(playerName, out _);
                PartyUpdated?.Invoke(null, EventArgs.Empty);
            }
            else
            {
                float cur = root.TryGetProperty("currentHp", out var cProp) && cProp.ValueKind == JsonValueKind.Number
                    ? cProp.GetSingle() : 100f;
                float max = root.TryGetProperty("maxHp", out var mProp) && mProp.ValueKind == JsonValueKind.Number
                    ? mProp.GetSingle() : 100f;

                var member = PartyMembers.GetOrAdd(playerName, _ => new PartyMemberHp { Username = playerName });
                member.CurrentHp = cur;
                member.MaxHp = max;
                member.LastUpdated = DateTime.Now;

                if (userId > 0 && member.UserId == 0)
                {
                    member.UserId = userId;
                    string? avatar = await GetPlayerAvatarUrlAsync(userId).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(avatar))
                        member.AvatarUrl = avatar;
                }

                PartyUpdated?.Invoke(null, EventArgs.Empty);
            }
        }

        private static async Task BroadcastLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    float simulatedHp = 97f;
                    float simulatedMaxHp = 100f;

                    string logPath = Path.Combine(Paths.DataRoot, "party_hp_local.json");
                    if (File.Exists(logPath))
                    {
                        string text = await File.ReadAllTextAsync(logPath, token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.TryGetProperty("currentHp", out var c)) simulatedHp = c.GetSingle();
                        if (doc.RootElement.TryGetProperty("maxHp", out var m)) simulatedMaxHp = m.GetSingle();
                    }

                    string selfName = _cachedPlayerName ?? Environment.UserName;
                    long selfUserId = _cachedPlayerUserId;

                    var selfMember = PartyMembers.GetOrAdd(selfName,
                        _ => new PartyMemberHp { Username = selfName, UserId = selfUserId });
                    if (selfMember.UserId == 0 && selfUserId > 0) selfMember.UserId = selfUserId;
                    if (string.IsNullOrEmpty(selfMember.AvatarUrl) && selfUserId > 0)
                    {
                        string? myAvatar = await GetPlayerAvatarUrlAsync(selfUserId).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(myAvatar)) selfMember.AvatarUrl = myAvatar;
                    }
                    selfMember.CurrentHp = simulatedHp;
                    selfMember.MaxHp = simulatedMaxHp;
                    PartyUpdated?.Invoke(null, EventArgs.Empty);

                    await SendInternalAsync(ws, simulatedHp, simulatedMaxHp, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    App.Logger.Debug($"[PartyHP] Broadcast iteration error: {ex.Message}");
                }

                try { await Task.Delay(1000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
