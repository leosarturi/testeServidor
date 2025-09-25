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
using System.Diagnostics;

namespace ServidorLocal
{
    public class Program
    {
        // -------------------- Estado --------------------
        private static readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private static readonly ConcurrentDictionary<string, PlayerData> _players = new();
        private static readonly ConcurrentDictionary<string, string> _playersMap = new();
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        private static SpawnData area1 = new(0f, 0f, 0, 0, Array.Empty<MobData>());


        public static event Action<string>? OnPlayerConnected;
        public static event Action<string>? OnPlayerDisconnected;

        // -------------------- Skill Config --------------------
        private static readonly HashSet<string> _validActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "aa",
            "s1",
            "s2"
        };
        private const bool NormalizeSkillDirection = false;

        // -------------------- Skill helpers --------------------
        private static bool IsValidSkillAction(string? action) =>
            !string.IsNullOrWhiteSpace(action) && _validActions.Contains(action!);

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

            var json = JsonSerializer.Serialize(payload);
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
            _ = RunSpawnLoopAsync(app.Lifetime.ApplicationStopping);

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
            await BroadcastPlayerConnectedAsync(clientId, ct);
            await ChangeMap(clientId, "cidade", ct);
            await HandleClientLoopAsync(socket, clientId, ct);

        }

        private static async Task ChangeMap(string clientId, string map, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(map)) return;

            _playersMap.TryGetValue(clientId, out var oldMap);
            Console.WriteLine($"Cliente {clientId} trocou de '{oldMap}' para '{map}'");
            var player = _players[clientId];
            _players.TryUpdate(clientId, new PlayerData(player.idplayer, player.posx, player.posy, map), player);
            _playersMap[clientId] = map;

            // Atualiza a foto do mapa antigo (alguém saiu) e do novo mapa (alguém entrou)
            if (!string.IsNullOrEmpty(oldMap))
                await BroadcastPlayersOfMapAsync(oldMap, ct);

            await BroadcastPlayersOfMapAsync(map, ct);
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
            _players.TryAdd(clientId, new PlayerData(clientId, 0, 0, "cidade"));
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
                head = JsonSerializer.Deserialize<EnvelopeTypeOnly>(msg);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(head.type)) return;

            switch (head.type.ToLowerInvariant())
            {
                case "trocar_mapa":
                    try
                    {

                        SocketEnvelope<MapData> envelope = JsonSerializer.Deserialize<SocketEnvelope<MapData>>(msg);
                        Console.WriteLine(msg);
                        await ChangeMap(envelope.data.idplayer, envelope.data.mapa, ct);
                        return;
                    }
                    catch
                    {

                    }
                    break;
                case "skill":
                    {
                        SocketEnvelope<List<SkillCastInput>>? env = null;
                        try
                        {
                            env = JsonSerializer.Deserialize<SocketEnvelope<List<SkillCastInput>>>(msg);
                        }
                        catch { }

                        if (env is null || env.Value.data is null) return;

                        foreach (var input in env.Value.data)
                        {
                            if (!IsValidSkillAction(input.action)) continue;

                            var (dx, dy) = NormalizeSkillDirection ? Normalize(input.dx, input.dy) : (input.dx, input.dy);
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
                        SocketEnvelope<List<PlayerData>>? env = null;
                        try
                        {
                            env = JsonSerializer.Deserialize<SocketEnvelope<List<PlayerData>>>(msg);
                        }
                        catch { }

                        if (env is null || env.Value.data is null) return;

                        foreach (var item in env.Value.data)
                        {
                            var coerced = item with { idplayer = clientId };
                            _players[clientId] = coerced;
                        }

                        await BroadcastAllPlayersAsync(clientId, ct);
                        return;

                    }
                case "mob":
                    {
                        SocketEnvelope<List<MobData>>? env = null;
                        try
                        {
                            env = JsonSerializer.Deserialize<SocketEnvelope<List<MobData>>>(msg);
                        }
                        catch { }

                        if (env is null || env.Value.data is null) return;
                        area1.Mobs = env.Value.data.ToArray();
                        await BroadcastAllAsync(JsonSerializer.Serialize(msg), ct);
                        return;
                    }
                default:
                    return;
            }
        }

        // -------------------- Broadcasts --------------------
        private static async Task BroadcastAllPlayersAsync(string clientId, CancellationToken ct)
        {
            try
            {


                foreach (var kvp in _clients)
                {
                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        var data = JsonSerializer.Serialize(
                            new
                            {
                                type = "player",
                                data = _players.Values.ToList()
                            },
                            _json
                        );

                        var bytes = Encoding.UTF8.GetBytes(data);
                        await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no broadcast de players: {ex.Message}");
            }
        }

        private static async Task BroadcastPlayerConnectedAsync(string clientId, CancellationToken ct)
        {


            foreach (var kvp in _clients)
            {
                foreach (var pToSend in _clients)
                {
                    Console.WriteLine($"Cliente {clientId} conectou enviando para {pToSend}");
                    var message = JsonSerializer.Serialize(new { type = "connect", idplayer = pToSend.Key });
                    var bytes = Encoding.UTF8.GetBytes(message);

                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        try
                        {
                            await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                        }
                        catch { /* ignore */ }
                    }
                }
            }

        }

        private static async Task BroadcastPlayerDisconnectedAsync(string clientId, CancellationToken ct)
        {
            var message = JsonSerializer.Serialize(new { type = "disconnect", idplayer = clientId });
            await BroadcastRawAsync(message, null, ct);
        }
        private static async Task BroadcastPlayerChangedMapAsync(string clientId, string? oldMap, CancellationToken ct)
        {
            var message = JsonSerializer.Serialize(new { type = "disconnect", idplayer = clientId });
            if (oldMap == null) return;
            await BroadMapChangedAsync(message, clientId, oldMap, ct);
        }

        private static async Task BroadcastRawAsync(string text, string? excludeClientId, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            foreach (var kvp in _clients)
            {
                if (excludeClientId is not null && kvp.Key == excludeClientId) continue;
                if (excludeClientId is not null && _playersMap[kvp.Key] != _playersMap[excludeClientId]) continue;

                if (kvp.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }
                    catch { /* ignore */ }
                }
            }
        }

        private static async Task BroadcastAllAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            foreach (var kvp in _clients)
            {
                if (kvp.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        if (_playersMap[kvp.Key] != "mapa") return;
                        await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }
                    catch { /* ignore */ }
                }
            }
        }

        private static async Task BroadcastPlayersOfMapAsync(string map, CancellationToken ct)
        {
            try
            {
                var playersOfMap = _players.Values
                    .Where(p => _playersMap.TryGetValue(p.idplayer, out var m) && m == map)
                    .ToList();

                var message = JsonSerializer.Serialize(new
                {
                    type = "trocar_mapa",
                    data = playersOfMap
                }, _json);

                var bytes = Encoding.UTF8.GetBytes(message);

                foreach (var kvp in _clients)
                {
                    if (_playersMap.TryGetValue(kvp.Key, out var cm) && cm == map && kvp.Value.State == WebSocketState.Open)
                    {
                        try { await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
                        catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no broadcast de players do mapa '{map}': {ex.Message}");
            }
        }

        private static async Task BroadMapChangedAsync(string text, string? excludeClientId, string oldMap, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            foreach (var kvp in _clients)
            {
                if (excludeClientId is not null && kvp.Key == excludeClientId) continue;
                if (excludeClientId is not null && _playersMap[kvp.Key] != oldMap) continue;

                if (kvp.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }
                    catch { /* ignore */ }
                }
            }
        }

        // --- config do loop ---
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
        private const int MaxMobs = 50;
        private const int MaxPerTick = 5;

        // --- loop em background ---
        private static async Task RunSpawnLoopAsync(CancellationToken stop)
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stop))
            {
                // envie o delta ou o estado — aqui vou manter seu exemplo simples:
                var data = new { type = "mob", data = area1.Mobs };
                var json = JsonSerializer.Serialize(data);

                // IMPORTANTE: não passe "a" se não for um clientId válido
                await BroadcastAllAsync(json, stop);
                try { TickSpawn(stop); }
                catch { /* log opcional */ }

            }
        }

        // --- uma passada do loop ---
        private static void TickSpawn(CancellationToken ct)
        {
            // snapshot
            var snap = area1;
            var current = snap.SpawnedMob;
            if (current >= MaxMobs) return;

            var toSpawn = Math.Min(MaxMobs - current, MaxPerTick);

            // garanta lista mutável
            var list = (snap.Mobs ?? Array.Empty<MobData>()).ToList();

            // gere os novos mobs
            for (int i = 0; i < toSpawn; i++)
            {
                var mob = new MobData(
                    Guid.NewGuid().ToString(),
                    Random.Shared.Next(50),
                    Random.Shared.Next(50),
                    5
                );
                list.Add(mob);
            }

            // substitui o struct inteiro (record struct é imutável)
            area1 = snap with
            {
                SpawnedMob = snap.SpawnedMob + toSpawn,
                LastSpawnedTime = Environment.TickCount,
                Mobs = list.ToArray()
            };

            // envie o delta ou o estado — aqui vou manter seu exemplo simples:
            var data = new { type = "mob", data = area1.Mobs };
            var json = JsonSerializer.Serialize(data);


        }



    }

}




