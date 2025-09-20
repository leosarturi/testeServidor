using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using ServidorLocal.Domain;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ServidorLocal
{
    public class Program
    {
        // -------------------- Estado --------------------
        private static readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private static readonly ConcurrentDictionary<string, PlayerData> _players = new();
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        public static event Action<string>? OnPlayerConnected;
        public static event Action<string>? OnPlayerDisconnected;

        // -------------------- Skill Config --------------------
        private static readonly HashSet<string> _validActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "aa", "s1", "s2"
        };
        private const bool NormalizeSkillDirection = true;

        // -------------------- Skill helpers --------------------
        private static bool IsValidSkillAction(string? action)
            => !string.IsNullOrWhiteSpace(action) && _validActions.Contains(action!);

        private static (float x, float y) Normalize(float x, float y)
        {
            var len = MathF.Sqrt(x * x + y * y);
            if (len <= 0.0001f) return (0f, 0f);
            return (x / len, y / len);
        }

        // -------------------- Broadcasts de Skill --------------------
        private static async Task BroadcastSkillAsync(SkillCast skill, string excludeClientId, CancellationToken ct)
        {
            var payload = new
            {
                type = "skill",
                data = new[]
                {
                    new
                    {
                        idplayer = skill.idplayer,
                        action = skill.action,
                        dir = new { x = skill.dx, y = skill.dy },
                        ts = skill.tsUtcMs
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, _json);
            await BroadcastRawAsync(json, excludeClientId, ct);
        }



        // -------------------- Startup --------------------
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://0.0.0.0:443");

            var app = builder.Build();
            app.UseWebSockets();

            OnPlayerConnected += id => Console.WriteLine($"[Evento] Player conectado: {id}");
            OnPlayerDisconnected += id => Console.WriteLine($"[Evento] Player desconectado: {id}");

            app.Map("/ws", HandleWebSocketAsync);

            app.Run();
        }

        // -------------------- Roteamento principal --------------------
        private static async Task HandleWebSocketAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket expected");
                return;
            }

            WebSocket? socket = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao aceitar WebSocket: {ex.Message}");
                return;
            }

            var ct = context.RequestAborted;

            var clientId = await ReceiveFirstMessageAsClientIdAsync(socket, ct);
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = Guid.NewGuid().ToString();

            await SendClientIdAsync(socket, clientId, ct);

            RegisterClient(clientId, socket);
            _ = BroadcastPlayerConnectedAsync(clientId, ct);

            await HandleClientLoopAsync(socket, clientId, ct);
        }

        // -------------------- Handshake --------------------
        private static async Task<string> ReceiveFirstMessageAsClientIdAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[1024];
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return string.Empty;

                var initialMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                return initialMsg?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro recebendo mensagem inicial: {ex.Message}");
                return string.Empty;
            }
        }

        private static async Task SendClientIdAsync(WebSocket socket, string clientId, CancellationToken ct)
        {
            try
            {
                var idBytes = Encoding.UTF8.GetBytes(clientId);
                await socket.SendAsync(idBytes, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro enviando UUID: {ex.Message}");
            }
        }

        // -------------------- Registro / limpeza --------------------
        private static void RegisterClient(string clientId, WebSocket socket)
        {
            _clients[clientId] = socket;
            OnPlayerConnected?.Invoke(clientId);
        }

        private static async Task SafeCloseAndCleanupAsync(string clientId, WebSocket socket, CancellationToken ct)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechando", ct);
            }
            catch { }

            _clients.TryRemove(clientId, out _);
            _players.TryRemove(clientId, out _);

            OnPlayerDisconnected?.Invoke(clientId);
            _ = BroadcastPlayerDisconnectedAsync(clientId, ct);
        }

        // -------------------- Loop de mensagens --------------------
        private static async Task HandleClientLoopAsync(WebSocket socket, string clientId, CancellationToken ct)
        {
            var buffer = new byte[4096];

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    using var ms = new MemoryStream();

                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await SafeCloseAndCleanupAsync(clientId, socket, ct);
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            ms.Write(buffer, 0, result.Count);
                        }
                    } while (!result.EndOfMessage);

                    if (ms.Length == 0) continue;

                    var msg = Encoding.UTF8.GetString(ms.ToArray());
                    await HandleTextMessageAsync(clientId, msg, ct);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro cliente {clientId}: {ex.Message}");
                await SafeCloseAndCleanupAsync(clientId, socket, ct);
            }
        }

        // -------------------- Tratamento de texto --------------------
        private static async Task HandleTextMessageAsync(string clientId, string msg, CancellationToken ct)
        {
            EnvelopeTypeOnly head;
            try
            {
                head = JsonSerializer.Deserialize<EnvelopeTypeOnly>(msg, _json);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(head.type))
                return;

            switch (head.type.ToLowerInvariant())
            {
                case "skill":
                    {
                        // { type: "skill", data: [ { action, dx, dy }, ... ] }
                        SocketEnvelope<List<SkillCastInput>>? env = null;
                        try
                        {
                            env = JsonSerializer.Deserialize<SocketEnvelope<List<SkillCastInput>>>(msg, _json);
                        }
                        catch { }

                        if (env is null || env.Value.data is null) return;

                        foreach (var input in env.Value.data)
                        {
                            if (!IsValidSkillAction(input.action))
                                continue;

                            var (dx, dy) = NormalizeSkillDirection
                                ? Normalize(input.dx, input.dy)
                                : (input.dx, input.dy);

                            var skill = new SkillCast(
                                idplayer: clientId,
                                action: input.action!.ToLowerInvariant(),
                                dx: dx,
                                dy: dy,
                                tsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            );

                            await BroadcastSkillAsync(skill, excludeClientId: clientId, ct);
                        }
                        return;
                    }

                case "player":
                    {
                        // { type: "player", data: [ { posx, posy }, ... ] }
                        SocketEnvelope<List<PlayerData>>? env = null;
                        try
                        {
                            env = JsonSerializer.Deserialize<SocketEnvelope<List<PlayerData>>>(msg, _json);
                        }
                        catch { }

                        if (env is null || env.Value.data is null) return;

                        foreach (var item in env.Value.data)
                        {
                            var coerced = item with { idplayer = clientId };
                            _players[clientId] = coerced; // usa o último como estado atual
                        }

                        await BroadcastAllPlayersAsync(ct);
                        return;
                    }

                default:
                    return;
            }
        }

        // -------------------- Broadcasts --------------------
        private static async Task BroadcastAllPlayersAsync(CancellationToken ct)
        {
            try
            {
                var data = JsonSerializer.Serialize(
                    new { type = "player", data = _players.Values.ToList() }, _json);
                var bytes = Encoding.UTF8.GetBytes(data);

                foreach (var kvp in _clients)
                {
                    if (kvp.Value.State == WebSocketState.Open)
                        await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no broadcast de players: {ex.Message}");
            }
        }

        private static async Task BroadcastPlayerConnectedAsync(string clientId, CancellationToken ct)
        {
            var message = JsonSerializer.Serialize(new { type = "connect", idplayer = clientId }, _json);
            await BroadcastRawAsync(message, null, ct);
        }

        private static async Task BroadcastPlayerDisconnectedAsync(string clientId, CancellationToken ct)
        {
            var message = JsonSerializer.Serialize(new { type = "disconnect", idplayer = clientId }, _json);
            await BroadcastRawAsync(message, null, ct);
        }


        private static async Task BroadcastRawAsync(string text, string? excludeClientId, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            foreach (var kvp in _clients)
            {
                if (excludeClientId is not null && kvp.Key == excludeClientId) continue;
                if (kvp.Value.State == WebSocketState.Open)
                {
                    try { await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
                    catch { /* ignore */ }
                }
            }
        }
    }
}
