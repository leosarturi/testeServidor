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
        private static readonly ConcurrentDictionary<string, string> _playersMap = new();
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        private static readonly ConcurrentDictionary<string, long> _mobLastAttackAt = new();

        private static readonly ConcurrentQueue<object> _forcedMobUpdates = new();



        // Config de spawn por "área" (quadrantes). Todos no mesmo mapa "mapa".
        private static SpawnData area1 = new(50f, 50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area2 = new(-50f, 50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area3 = new(50f, -50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area4 = new(-50f, -50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData[] _spawnConfigs = new[] { area1, area2, area3, area4 };

        private readonly record struct MobDamageInput(string idmob, float damage);
        private sealed class MobHitInput { public string idmob { get; set; } = ""; public float dmg { get; set; } }


        private sealed class PartySetInput { public string? partyId { get; set; } }

        // Party state
        // playerId -> partyId (ou null)
        private static readonly ConcurrentDictionary<string, string?> _partyOfPlayer = new();
        // partyId -> membros
        private static readonly ConcurrentDictionary<string, HashSet<string>> _partyMembers =
            new(StringComparer.OrdinalIgnoreCase);

        // Estado autoritativo de mobs por mapa (swap atômico de referência)
        private sealed record AreaState(string Map, int Version, MobData[] Mobs);
        private static volatile Dictionary<string, AreaState> _areas =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["mapa"] = new AreaState("mapa", 0, Array.Empty<MobData>()),
                ["cidade"] = new AreaState("cidade", 0, Array.Empty<MobData>())
            };

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
            _ = RunGameLoopAsync(app.Lifetime.ApplicationStopping);

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
            var init = await ReceiveFirstMessageAsync(socket, ct);
            Console.WriteLine($" mensagem inicial {init.Value.idplayer}");
            if (init == null || init.Value.idplayer == null)
            {

                return;
            }

            await SendClientIdAsync(socket, init.Value.idplayer, ct);
            RegisterClient(init.Value, socket);

            // snapshot inicial para o mapa do jogador
            if (_playersMap.TryGetValue(init.Value.idplayer, out var map))
            {
                // await SendMobSnapshotAsync(socket, map, ct);

            }
            await BroadcastPlayerConnectedAsync(_players[init.Value.idplayer], ct);
            await HandleClientLoopAsync(socket, init.Value.idplayer, ct);
        }

        // -------------------- Helper de normalização (wire DTO) --------------------
        private static object ToWire(MobData m) => new
        {
            idmob = m.idmob,
            posx = m.posx,
            posy = m.posy,
            life = m.life,
            maxlife = m.maxlife,
            tipo = m.tipo,
            area = m.area
        };

        // -------------------- Mapa / Snapshot --------------------
        private static async Task ChangeMap(string clientId, string map, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(map)) return;

            _playersMap.TryGetValue(clientId, out var oldMap);
            Console.WriteLine($"Cliente {clientId} trocou de '{oldMap}' para '{map}'");

            if (_players.TryGetValue(clientId, out var player))
            {
                _players[clientId] = player with { mapa = map };
            }
            _playersMap[clientId] = map;

            // Atualiza listas de players por mapa
            if (!string.IsNullOrEmpty(oldMap))
                await BroadcastPlayersOfMapAsync(oldMap, ct);

            await BroadcastPlayersOfMapAsync(map, ct);

            // Envia snapshot de mobs desse mapa somente para o cliente que trocou
            if (_clients.TryGetValue(clientId, out var ws) && ws.State == WebSocketState.Open)
            {
                await SendMobSnapshotAsync(ws, map, ct);
            }
        }

        private static async Task SendMobSnapshotAsync(WebSocket socket, string map, CancellationToken ct)
        {
            if (!_areas.TryGetValue(map, out var area)) return;

            var payload = new
            {
                type = "mob_state",
                map = area.Map,
                v = area.Version,
                mobs = area.Mobs.Select(ToWire).ToArray()
            };
            var json = JsonSerializer.Serialize(payload, _json);

            Console.WriteLine($"[Mob] SEND SNAPSHOT map={area.Map} v={area.Version} count={area.Mobs.Length}");

            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
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

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var msg = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine(msg);
                var initData = JsonSerializer.Deserialize<PlayerData>(msg);
                Console.WriteLine(initData);
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
        private static void RegisterClient(PlayerData player, WebSocket socket)
        {
            _clients[player.idplayer] = socket;
            _players.TryAdd(player.idplayer, player);
            _playersMap.TryAdd(player.idplayer, player.mapa ?? "cidade");
            OnPlayerConnected?.Invoke(player.idplayer);

            // envia a lista de players do mapa atual para ele (e o connect para os demais, se quiser)
            //  _ = BroadcastPlayersOfMapAsync(_playersMap[player.idplayer], CancellationToken.None);
        }

        private static async Task SafeCloseAndCleanupAsync(string clientId, WebSocket socket, CancellationToken ct)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechando", ct);
            }
            catch { /* ignore */ }

            _clients.TryRemove(clientId, out _);
            _players.TryRemove(clientId, out var removed);
            _playersMap.TryRemove(clientId, out _);
            _partyOfPlayer.TryRemove(clientId, out _);
            _partyMembers.TryRemove(clientId, out _);

            OnPlayerDisconnected?.Invoke(clientId);
            await BroadcastPlayerDisconnectedAsync(removed, ct);
            socket.Dispose();
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


        private static MobData MoveMobAI(MobData mob, List<PlayerData> playersInArea, CancellationToken ct)
        {
            const float speed = 0.5f;        // velocidade do mob
            const float aggroRange = 10f;    // distância máxima para perseguir o player
            const float attackRange = 1.6f;  // distância para ataque     // dano base
            const int attackCooldownMs = 2000;

            float dx = 0f, dy = 0f;

            if (playersInArea.Count > 0)
            {
                PlayerData? target = null;
                float closest = float.MaxValue;

                // Procura o player mais próximo
                foreach (var p in playersInArea)
                {
                    var dist = MathF.Sqrt(
                        (p.posx - mob.posx) * (p.posx - mob.posx) +
                        (p.posy - mob.posy) * (p.posy - mob.posy)
                    );

                    if (dist < closest)
                    {
                        closest = dist;
                        target = p;
                    }
                }

                // Se encontrou e está no raio de aggro
                if (target != null && closest <= aggroRange)
                {
                    // Movimento em direção ao player
                    dx = target.Value.posx - mob.posx;
                    dy = target.Value.posy - mob.posy;

                    // Normaliza para manter velocidade constante
                    var len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len > 0.0001f)
                    {
                        dx = dx / len * speed;
                        dy = dy / len * speed;
                    }

                    if (closest <= attackRange)
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var last = _mobLastAttackAt.TryGetValue(mob.idmob, out var v) ? v : 0L;

                        if (now - last >= attackCooldownMs)
                        {
                            _mobLastAttackAt[mob.idmob] = now; // inicia cooldown
                            var data = new { type = "mob_attack", data = mob.idmob };
                            var json = JsonSerializer.Serialize(data);

                            _ = BroadcastAllAsync(json, target.Value.mapa, ct);
                            Console.WriteLine($"[MOB] {mob.idmob} atacou");
                        }
                    }
                }


                // Atualiza posição
                float newX = mob.posx + dx;
                float newY = mob.posy + dy;
                return mob with { posx = newX, posy = newY };
            }

            return mob;
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
                        await ChangeMap(envelope.data.idplayer, envelope.data.mapa, ct);
                        return;
                    }
                    catch { /* ignore */ }
                    break;

                case "mob_request":
                    {
                        // cliente pede snapshot do mapa atual
                        if (_playersMap.TryGetValue(clientId, out var map)
                            && _clients.TryGetValue(clientId, out var ws)
                            && ws.State == WebSocketState.Open)
                        {
                            await SendMobSnapshotAsync(ws, map, ct);
                        }
                        return;
                    }

                case "skill":
                    {
                        SocketEnvelope<List<SkillCastInput>>? env = null;
                        try { env = JsonSerializer.Deserialize<SocketEnvelope<List<SkillCastInput>>>(msg); }
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
                        try { env = JsonSerializer.Deserialize<SocketEnvelope<List<PlayerData>>>(msg); }
                        catch { }

                        if (env is null || env.Value.data is null) return;

                        foreach (var item in env.Value.data)
                        {
                            var coerced = item with { idplayer = clientId };
                            _players.AddOrUpdate(
                                coerced.idplayer,
                                coerced,
                                (key, oldValue) => coerced
                            );
                        }

                        await BroadcastAllPlayersAsync(clientId, ct);
                        return;
                    }


                case "party_set":
                    {
                        try
                        {
                            var env = JsonSerializer.Deserialize<SocketEnvelope<PartySetInput>>(msg);
                            if (env.data != null)
                                await SetPartyAsync(clientId,
                                    string.IsNullOrWhiteSpace(env.data.partyId) ? null : env.data.partyId!.Trim(),
                                    ct);
                        }
                        catch { /* ignore */ }
                        return;
                    }

                case "mob_hit":
                    {
                        try
                        {

                            var env = JsonSerializer.Deserialize<SocketEnvelope<MobHitInput>>(msg);
                            if (env.data == null) return;
                            Console.WriteLine(msg);
                            if (!_playersMap.TryGetValue(clientId, out var map)) return;
                            if (!_areas.TryGetValue(map, out var areaState)) return;

                            // aplica dano no mob certo
                            var mobs = areaState.Mobs.ToList();
                            var idx = mobs.FindIndex(m => m.idmob == env.data.idmob);
                            if (idx == -1) return;

                            var mob = mobs[idx];
                            var newLife = Math.Max(0, mob.life - env.data.dmg);
                            bool died = newLife <= 0;

                            List<MobData> updates = new();
                            List<string> removes = new();
                            if (died)
                            {
                                removes.Add(mob.idmob);
                                mobs.RemoveAt(idx);
                            }
                            else
                            {
                                mob = mob with { life = newLife };
                                mobs[idx] = mob;
                                updates.Add(new MobData { idmob = mob.idmob, posx = mob.posx, posy = mob.posy, life = mob.life, tipo = mob.tipo, area = mob.area });
                            }

                            // commit nova versão da área
                            var newArea = areaState with { Version = areaState.Version + 1, Mobs = mobs.ToArray() };
                            var newDict = new Dictionary<string, AreaState>(_areas, StringComparer.OrdinalIgnoreCase) { [map] = newArea };
                            _areas = newDict;
                            Console.WriteLine(updates.Count);
                            // broadcast delta
                            var deltaPayload = new
                            {
                                type = "mob_delta",
                                map = newArea.Map,
                                v = newArea.Version,
                                adds = Array.Empty<object>(),
                                updates,
                                removes
                            };
                            var jsonDelta = JsonSerializer.Serialize(deltaPayload);
                            await BroadcastAllAsync(jsonDelta, map, ct);

                            // XP se morreu
                            if (died)
                            {
                                var xp = ComputeMobXp(mob);
                                await AwardXpAsync(clientId, xp, map, ct);
                            }
                        }
                        catch { /* ignore */ }
                        return;
                    }

                default:
                    return;
            }
        }

        /* -------------------- PARTY -------------------- */

        private static async Task SetPartyAsync(string playerId, string? partyIdOrNull, CancellationToken ct)
        {
            // 1) remover da party anterior (se houver)
            if (_partyOfPlayer.TryGetValue(playerId, out var prev) && !string.IsNullOrWhiteSpace(prev))
            {
                if (_partyMembers.TryGetValue(prev!, out var oldSet))
                {
                    lock (oldSet) oldSet.Remove(playerId);
                    // avisa o cara que saiu
                    var leftPayload = new { type = "party_info", data = new { party = "", members = Array.Empty<string>() } };
                    await SendToClientAsync(playerId, JsonSerializer.Serialize(leftPayload, _json), ct);

                    // avisa os que ficaram
                    var leftToOthers = new { type = "party_info", data = new { party = prev, members = oldSet.ToArray() } };
                    var jsonLeftToOthers = JsonSerializer.Serialize(leftToOthers, _json);
                    foreach (var m in oldSet) await SendToClientAsync(m, jsonLeftToOthers, ct);
                }
            }

            // 2) se foi só sair, encerra
            if (string.IsNullOrWhiteSpace(partyIdOrNull))
            {
                _partyOfPlayer[playerId] = null;
                return;
            }

            // 3) adicionar na nova party e avisar TODO MUNDO da party
            _partyOfPlayer[playerId] = partyIdOrNull;
            var set = _partyMembers.GetOrAdd(partyIdOrNull!, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (set) set.Add(playerId);

            var payload = new { type = "party_info", data = new { party = partyIdOrNull, members = set.ToArray() } };
            var json = JsonSerializer.Serialize(payload, _json);
            foreach (var m in set) await SendToClientAsync(m, json, ct);
        }


        private static int ComputeMobXp(in MobData m)
        {
            // regra simples: tipo e área influenciam
            var baseXp = 10 * (m.tipo + 1) + 5 * (m.area + 1);
            return Math.Max(5, baseXp);
        }

        private static async Task SendToClientAsync(string playerId, string text, CancellationToken ct)
        {
            if (_clients.TryGetValue(playerId, out var ws) && ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
            }
        }

        // Divide XP igualmente entre membros da party do killer (no mesmo mapa). Se não houver party, tudo para o killer.
        private static async Task AwardXpAsync(string killerId, int totalXp, string map, CancellationToken ct)
        {
            // Descobre se o killer tem party
            var partyId = _partyOfPlayer.TryGetValue(killerId, out var p) ? p : null;

            List<string> recipients;
            if (string.IsNullOrWhiteSpace(partyId))
            {
                // Sem party: XP inteiro para o killer
                recipients = new List<string> { killerId };
            }
            else
            {
                // Com party: pega só quem está conectado e no MESMO mapa
                if (_partyMembers.TryGetValue(partyId!, out var set))
                {
                    // Se seu HashSet não for thread-safe, use lock(set)
                    recipients = set
                        .Where(pid => _clients.ContainsKey(pid) &&
                                      _playersMap.TryGetValue(pid, out var m) && m == map)
                        .ToList();
                }
                else
                {
                    recipients = new List<string> { killerId };
                }

                // Se por algum motivo ninguém qualificar, garante pelo menos o killer
                if (recipients.Count == 0) recipients.Add(killerId);
            }

            // Divide igualmente entre os qualificados (1 => tudo; 2 => /2; 3 => /3; etc.)
            var count = Math.Max(1, recipients.Count);
            var per = Math.Max(1, totalXp / count); // parte fracionária é descartada (int)

            // Pacote único (mesmo 'per' para todos), enviado a cada recipient
            var announce = new
            {
                type = "xp_gain",
                who = killerId,
                xp = totalXp,
                per = per,
                to = recipients
            };
            var announceJson = JsonSerializer.Serialize(announce, _json);

            foreach (var r in recipients)
                await SendToClientAsync(r, announceJson, ct);
        }

        // -------------------- Broadcasts (Players) --------------------
        private static async Task BroadcastAllPlayersAsync(string clientId, CancellationToken ct)
        {
            try
            {
                foreach (var kvp in _clients)
                {
                    if (kvp.Value.State != WebSocketState.Open) continue;

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
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no broadcast de players: {ex.Message}");
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
                });

                var bytes = Encoding.UTF8.GetBytes(message);

                foreach (var kvp in _clients)
                {
                    if (kvp.Value.State != WebSocketState.Open) continue;
                    if (_playersMap.TryGetValue(kvp.Key, out var cm) && cm == map)
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
            await SetPartyAsync(clientId.idplayer, null, ct);
            await BroadcastRawAsync(message, null, ct);
        }
        // -------------------- Broadcast Genérico --------------------
        private static async Task BroadcastRawAsync(string text, string? excludeClientId, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            foreach (var kvp in _clients)
            {
                if (excludeClientId is not null && kvp.Key == excludeClientId) continue;

                if (excludeClientId is not null
                    && _playersMap.TryGetValue(excludeClientId, out var exMap)
                    && _playersMap.TryGetValue(kvp.Key, out var toMap)
                    && exMap != toMap)
                {
                    continue;
                }

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
                if (kvp.Value.State != WebSocketState.Open) continue;
                if (_playersMap.TryGetValue(kvp.Key, out var m) && m != map) continue;

                try
                {
                    await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
                catch { /* ignore */ }
            }
        }

        // -------------------- Loop do jogo (mobs autoritativos) --------------------
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
        private const int MaxMobsPerArea = 15;
        private const int MaxPerTickPerArea = 5;

        private static async Task RunGameLoopAsync(CancellationToken stop)
        {
            using var timer = new PeriodicTimer(TickInterval);

            while (await timer.WaitForNextTickAsync(stop))
            {
                var map = "mapa";

                // Snapshot antigo
                var oldAreas = _areas;
                var oldArea = oldAreas.TryGetValue(map, out var a)
                    ? a
                    : new AreaState(map, 0, Array.Empty<MobData>());

                // Gera novo array de mobs aplicando spawn por área (somente adicionar; regra simples)
                var newMobs = UpdateMobsByAreas(oldArea.Mobs, stop);

                // Calcula delta
                var (adds, updates, removes, changed) = Diff(oldArea.Mobs, newMobs);

                // drena updates forçados (ex.: action:"attack")
                var forced = new List<object>();
                while (_forcedMobUpdates.TryDequeue(out var u))
                    forced.Add(u);

                // Só envia se houve mudança real ou ações forçadas
                if (changed || forced.Count > 0)
                {
                    var newArea = oldArea with
                    {
                        Version = oldArea.Version + 1,
                        Mobs = newMobs
                    };

                    // swap dicionário
                    var newDict = new Dictionary<string, AreaState>(oldAreas, StringComparer.OrdinalIgnoreCase)
                    {
                        [map] = newArea
                    };
                    _areas = newDict;

                    var deltaPayload = new
                    {
                        type = "mob_delta",
                        map = newArea.Map,
                        v = newArea.Version,
                        adds = adds.Select(ToWire).ToArray(),
                        updates = updates.Concat(forced).ToArray(), // inclui ações (attack)
                        removes
                    };

                    var json = JsonSerializer.Serialize(deltaPayload, _json);
                    Console.WriteLine($"[Spawn] v={newArea.Version} adds={adds.Count} updates={updates.Count + forced.Count} removes={removes.Count}");
                    await BroadcastAllAsync(json, map, stop);
                }
            }
        }

        // Gera a nova lista de mobs por áreas, respeitando limites por área.
        private static MobData[] UpdateMobsByAreas(MobData[] currentAll, CancellationToken ct)
        {
            // Agrupa os mobs atuais por área (0..3)
            var byArea = currentAll
                .GroupBy(m => m.area)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (int areaIndex = 0; areaIndex < _spawnConfigs.Length; areaIndex++)
            {
                if (!byArea.TryGetValue(areaIndex, out var list))
                {
                    list = new List<MobData>();
                    byArea[areaIndex] = list;
                }

                var curr = list.Count;
                if (curr >= MaxMobsPerArea)
                {

                    for (int i = 0; i < list.Count; i++)
                    {
                        // Pega players no mesmo mapa e área
                        var playersInArea = _players.Values
                            .Where(p => _playersMap.TryGetValue(p.idplayer, out var m) && m == _spawnConfigs[areaIndex].Mapa)
                            .ToList();

                        list[i] = MoveMobAI(list[i], playersInArea, ct);
                    }
                    continue;
                }

                var toSpawn = Math.Min(MaxMobsPerArea - curr, MaxPerTickPerArea);
                var cfg = _spawnConfigs[areaIndex];

                for (int i = 0; i < toSpawn; i++)
                {
                    int x, y;

                    // X
                    if (cfg.PosX >= 0)
                        x = Random.Shared.Next(0, (int)cfg.PosX + 1);
                    else
                        x = Random.Shared.Next((int)cfg.PosX, 1);

                    // Y
                    if (cfg.PosY >= 0)
                        y = Random.Shared.Next(0, (int)cfg.PosY + 1);
                    else
                        y = Random.Shared.Next((int)cfg.PosY, 1);

                    var mob = new MobData(
                        Guid.NewGuid().ToString(),
                        x,   // implícito para float
                        y,   // implícito para float
                        100 * (areaIndex + 1),
                        100 * (areaIndex + 1),
                        Random.Shared.Next(4),
                        areaIndex
                    );

                    list.Add(mob);
                }
            }

            // Flatten
            return byArea.OrderBy(k => k.Key).SelectMany(k => k.Value).ToArray();
        }

        // Calcula delta entre listas (por id). Updates consideram mudança em x,y,life,tipo,area.
        private static (List<MobData> adds, List<object> updates, List<string> removes, bool changed)
            Diff(MobData[] oldArr, MobData[] newArr)
        {
            var adds = new List<MobData>();
            var updates = new List<object>();
            var removes = new List<string>();
            bool changed = false;

            var oldById = oldArr.ToDictionary(m => m.idmob);
            var newById = newArr.ToDictionary(m => m.idmob);

            // Removidos
            foreach (var oldId in oldById.Keys)
                if (!newById.ContainsKey(oldId))
                {
                    removes.Add(oldId);
                    changed = true;
                }

            // Adicionados/Atualizados
            foreach (var kv in newById)
            {
                if (!oldById.TryGetValue(kv.Key, out var prev))
                {
                    adds.Add(kv.Value);
                    changed = true;
                }
                else
                {
                    var cur = kv.Value;
                    if (prev.posx != cur.posx || prev.posy != cur.posy || prev.life != cur.life || prev.tipo != cur.tipo || prev.area != cur.area)
                    {
                        // updates como "patch": envia somente campos mutáveis
                        updates.Add(new
                        {
                            idmob = cur.idmob,
                            posx = (prev.posx != cur.posx) ? cur.posx : (float?)null,
                            posy = (prev.posy != cur.posy) ? cur.posy : (float?)null,
                            life = (prev.life != cur.life) ? cur.life : (float?)null,
                            tipo = (prev.tipo != cur.tipo) ? cur.tipo : (int?)null,
                            area = (prev.area != cur.area) ? cur.area : (int?)null
                        });
                        changed = true;
                    }
                }
            }

            return (adds, updates, removes, changed);
        }
    }
}

/* Tipos auxiliares esperados (já existentes no seu projeto)
   - PlayerData: record com (idplayer, posx, posy, mapa, status)
   - MobData: record struct com (idmob, posx, posy, life, tipo, area)
   - SpawnData: record com (PosX, PosY, LastSpawnedTime, MobData[], Mapa)
   - EnvelopeTypeOnly, SocketEnvelope<T>, MapData, SkillCastInput, SkillCast
   Mantenha-os como já estão em ServidorLocal.Domain.
*/
