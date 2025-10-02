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

        private static SpawnData area1 = new(50f, 50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area2 = new(-50f, 50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area3 = new(50f, -50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area4 = new(-50f, -50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData[] listofareas = [area1, area2, area3, area4];


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
            var clientId = await ReceiveFirstMessageAsync(socket, ct);
            if (clientId == null) return;

            await SendClientIdAsync(socket, clientId.Value.idplayer, ct);
            RegisterClient((PlayerData)clientId, socket);
            await BroadcastPlayerConnectedAsync((PlayerData)clientId, ct);
            //  await ChangeMap(clientId, "cidade", ct);
            await HandleClientLoopAsync(socket, clientId.Value.idplayer, ct);






        }

        private static async Task ChangeMap(string clientId, string map, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(map)) return;

            _playersMap.TryGetValue(clientId, out var oldMap);
            Console.WriteLine($"Cliente {clientId} trocou de '{oldMap}' para '{map}'");
            var player = _players[clientId];
            _players.TryUpdate(clientId, new PlayerData(player.idplayer, player.posx, player.posy, map, player.status), player);
            _playersMap[clientId] = map;



            // Atualiza a foto do mapa antigo (alguém saiu) e do novo mapa (alguém entrou)
            if (!string.IsNullOrEmpty(oldMap))
                await BroadcastPlayersOfMapAsync(oldMap, ct);

            await BroadcastPlayersOfMapAsync(map, ct);


            foreach (var item in listofareas)
            {
                Console.WriteLine(item.Mapa);
                Console.WriteLine(item.Mobs.Count());
                if (map == item.Mapa)
                {
                    var data = new { type = "mob", data = item.Mobs, map = item.Mapa };
                    var json = JsonSerializer.Serialize(data);
                    Console.WriteLine(json);
                    // IMPORTANTE: não passe "a" se não for um clientId válido
                    //  _ = BroadcastAllAsync(json, item.Mapa, ct);
                }
            }

        }

        // -------------------- Handshake --------------------
        private static async Task<PlayerData?> ReceiveFirstMessageAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[1024];
            using var ms = new MemoryStream();
            try
            {


                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    Console.WriteLine($"Received first message: {result.Count}");
                    Console.WriteLine($"Received first message: {result.EndOfMessage}");
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var msg = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine($"{msg}");
                var initData = JsonSerializer.Deserialize<PlayerData>(msg);
                return initData;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro recebendo mensagem inicial: {ex.Message}");
                return null;
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
        private static void RegisterClient(PlayerData clientId, WebSocket socket)
        {
            _clients[clientId.idplayer] = socket;
            _players.TryAdd(clientId.idplayer, clientId);
            _playersMap.TryAdd(clientId.idplayer, "cidade");
            OnPlayerConnected?.Invoke(clientId.idplayer);
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
            var dataTosend = _players[clientId];
            _players.TryRemove(clientId, out _);
            OnPlayerDisconnected?.Invoke(clientId);
            _ = BroadcastPlayerDisconnectedAsync(dataTosend, ct);
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
                            _players.AddOrUpdate(
    coerced.idplayer,          // chave
    coerced,                   // valor a adicionar se não existir
    (key, oldValue) => coerced // valor a atualizar se já existir
);



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
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                        if (env is null || env.Value.data is null) return;
                        var a1 = env.Value.data.Where((x) =>
                        {
                            if (x.area == 0 && x.life > 0) return true;
                            return false;
                        }).ToArray();
                        var a2 = env.Value.data.Where(x => x.area == 1 && x.life > 0).ToArray();
                        var a3 = env.Value.data.Where(x => x.area == 2 && x.life > 0).ToArray();
                        var a4 = env.Value.data.Where(x => x.area == 3 && x.life > 0).ToArray();
                        area1.Mobs = a1;
                        area2.Mobs = a2;
                        area3.Mobs = a3;
                        area4.Mobs = a4;
                        listofareas = [area1, area2, area3, area4];

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

        private static async Task BroadcastPlayerConnectedAsync(PlayerData clientId, CancellationToken ct)
        {


            foreach (var kvp in _clients)
            {
                foreach (var pToSend in _clients)
                {
                    Console.WriteLine($"Cliente {kvp.Key} conectou enviando para {pToSend}");
                    var message = JsonSerializer.Serialize(new { type = "connect", data = _players[pToSend.Key] });
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

        private static async Task BroadcastPlayerDisconnectedAsync(PlayerData clientId, CancellationToken ct)
        {
            var message = JsonSerializer.Serialize(new { type = "disconnect", data = clientId });
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

        private static async Task BroadcastAllAsync(string text, string map, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            foreach (var kvp in _clients)
            {

                if (kvp.Value.State == WebSocketState.Open)
                {
                    if (_playersMap[kvp.Key] != map) return;
                    try
                    {
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
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(2000);
        private const int MaxMobs = 15;
        private const int MaxPerTick = 5;

        // --- loop em background ---
        private static async Task RunSpawnLoopAsync(CancellationToken stop)
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stop))
            {

                // envie o delta ou o estado — aqui vou manter seu exemplo simples:

                foreach (var item in listofareas)
                {
                    var data = new { type = "mob", data = item.Mobs, map = item.Mapa };
                    var json = JsonSerializer.Serialize(data);
                    await BroadcastAllAsync(json, item.Mapa, stop);
                }
                // IMPORTANTE: não passe "a" se não for um clientId válido

                try { TickSpawn(stop); }
                catch { /* log opcional */ }

            }
        }

        // --- uma passada do loop ---
        private static void TickSpawn(CancellationToken ct)
        {
            // snapshot
            for (int j = 0; j < listofareas.Count(); j++)
            {
                var snap = listofareas[j];
                var current = snap.Mobs.Count();
                if (current >= MaxMobs) return;

                var toSpawn = Math.Min(MaxMobs - current, MaxPerTick);

                // garanta lista mutável
                var list = (snap.Mobs ?? Array.Empty<MobData>()).ToList();

                // gere os novos mobs
                for (int i = 0; i < toSpawn; i++)
                {
                    int x, y;

                    // Para X
                    if (snap.PosX >= 0)
                    {
                        x = Random.Shared.Next(0, (int)snap.PosX + 1);   // 0 até +50
                    }
                    else
                    {
                        x = Random.Shared.Next((int)snap.PosX, 1);       // -50 até 0
                    }

                    // Para Y
                    if (snap.PosY >= 0)
                    {
                        y = Random.Shared.Next(0, (int)snap.PosY + 1);
                    }
                    else
                    {
                        y = Random.Shared.Next((int)snap.PosY, 1);
                    }

                    var mob = new MobData(
                        Guid.NewGuid().ToString(),
                        x,
                        y,
                        100 * (j + 1),
                        Random.Shared.Next(4),
                        j
                    );
                    list.Add(mob);
                }

                // substitui o struct inteiro (record struct é imutável)
                listofareas[j] = snap with
                {
                    LastSpawnedTime = Environment.TickCount,
                    Mobs = list.ToArray()
                };
                Console.WriteLine(listofareas[j].Mobs.Count());

            }


        }
    }

}



public class InitMessage
{
    public required string ClientId { get; set; }
    public required string Classe { get; set; }
}

