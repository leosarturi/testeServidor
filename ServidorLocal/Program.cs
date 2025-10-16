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

        // -------------------- Auction house --------------------
        private static readonly string AUCTIONS_FILE = Path.Combine(AppContext.BaseDirectory, "auctions.json");
        private sealed record AuctionEntry(string ownerId, int price, JsonElement item, long createdAt);
        private static readonly List<AuctionEntry> _auctions = new();
        private static readonly object _auctionsLock = new();
        private static ConcurrentDictionary<string, int> sellsToSend = new();

        private sealed class SellInput { public required string ownerId { get; set; } public int price { get; set; } public required JsonElement item { get; set; } }
        private sealed class BuyInput { public required string buyerId { get; set; } public required int index { get; set; } }

        private static void LoadAuctions()
        {
            try
            {
                lock (_auctionsLock)
                {
                    if (!File.Exists(AUCTIONS_FILE)) return;
                    var json = File.ReadAllText(AUCTIONS_FILE);
                    var arr = JsonSerializer.Deserialize<List<AuctionEntry>>(json);
                    if (arr is not null)
                    {
                        _auctions.Clear();
                        _auctions.AddRange(arr);
                    }
                }
            }
            catch { /* ignore load errors */ }
        }

        private static void SaveAuctions()
        {
            try
            {
                lock (_auctionsLock)
                {
                    var json = JsonSerializer.Serialize(_auctions);
                    File.WriteAllText(AUCTIONS_FILE, json);
                }
            }
            catch { /* ignore save errors */ }
        }

        private static async Task SendJsonToPlayerAsync(string playerIdOrClientId, object payload, CancellationToken ct)
        {
            try
            {

                // fallback: treat input as clientKey
                if (_clients.TryGetValue(playerIdOrClientId, out var ws2) && ws2.State == WebSocketState.Open)
                {
                    var json = JsonSerializer.Serialize(payload);
                    var buffer = Encoding.UTF8.GetBytes(json);
                    await ws2.SendAsync(buffer, WebSocketMessageType.Text, true, ct);
                }
            }
            catch { /* ignore send errors */ }
        }

        private static async Task SendSaleToPlayerAsync(string playerIdOrClientId, object payload, CancellationToken ct, int chaos)
        {
            try
            {

                // fallback: treat input as clientKey
                if (_clients.TryGetValue(playerIdOrClientId, out var ws2) && ws2.State == WebSocketState.Open)
                {
                    var json = JsonSerializer.Serialize(payload);
                    var buffer = Encoding.UTF8.GetBytes(json);
                    await ws2.SendAsync(buffer, WebSocketMessageType.Text, true, ct);
                }
                else
                {
                    Console.WriteLine($"[AUCTION] Player {playerIdOrClientId} offline, queueing sell notification");
                    sellsToSend.AddOrUpdate(playerIdOrClientId, chaos, (key, old) => old + chaos);
                }
            }
            catch { /* ignore send errors */ }
        }
        private static async Task BroadcastJsonAsync(object payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload);
            var buffer = Encoding.UTF8.GetBytes(json);
            var tasks = new List<Task>();
            foreach (var kv in _clients)
            {
                var ws = kv.Value;
                if (ws?.State == WebSocketState.Open)
                {
                    tasks.Add(ws.SendAsync(buffer, WebSocketMessageType.Text, true, ct));
                }
            }
            try { await Task.WhenAll(tasks); } catch { /* ignore individual send errors */ }
        }

        // Config de spawn por "área" (quadrantes). Todos no mesmo mapa "mapa".
        private static SpawnData area1 = new(50f, 50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area2 = new(-50f, 50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area3 = new(50f, -50f, 0, Array.Empty<MobData>(), "mapa");
        private static SpawnData area4 = new(-50f, -50f, 0, Array.Empty<MobData>(), "mapa");

        private static SpawnData dg1Area = new(30f, 30f, 0, Array.Empty<MobData>(), "dg1");
        private static SpawnData dg2Area = new(30f, 30f, 0, Array.Empty<MobData>(), "dg2");
        private static SpawnData dg3Area = new(30f, 30f, 0, Array.Empty<MobData>(), "dg3");
        private static SpawnData dg4Area = new(30f, 30f, 0, Array.Empty<MobData>(), "dg4");
        private static SpawnData[] _spawnConfigs = new[] { area1, area2, area3, area4 };

        // Boss (por mapa)
        private static readonly Dictionary<string, long> _bossNextRespawnAtMs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dg1"] = 0,
            ["dg2"] = 0
        };

        private static readonly Dictionary<string, long> _bossLastCombatMs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dg1"] = 0,
            ["dg2"] = 0
        };

        private const int BossTipo = 99;
        private static readonly (int x, int y) BossSpawnDG1 = (10, 10);   // ajuste se quiser
        private static readonly (int x, int y) BossSpawnDG2 = (0, 0);



        private const int BossMaxLife = 1500;
        private const int BossRegenPerTick = 30;         // ~30 a cada 200ms => 150/s
        private static readonly TimeSpan BossOutOfCombat = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan BossRespawnDelay = TimeSpan.FromMinutes(3);

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
                ["cidade"] = new AreaState("cidade", 0, Array.Empty<MobData>()),
                ["dg1"] = new AreaState("dg1", 0, Array.Empty<MobData>()),
                ["dg2"] = new AreaState("dg2", 0, Array.Empty<MobData>()),
                ["dg3"] = new AreaState("dg3", 0, Array.Empty<MobData>()),
                ["dg4"] = new AreaState("dg4", 0, Array.Empty<MobData>())
            };

        private static readonly Dictionary<string, SpawnData[]> _spawnByMap = new()
        {
            ["mapa"] = new[] { area1, area2, area3, area4 },
            ["dg1"] = new[] { dg1Area },
            ["dg2"] = new[] { dg2Area },
            ["dg3"] = new[] { dg3Area },
            ["dg4"] = new[] { dg4Area },

            // "cidade" propositalmente sem entry para manter SEM mobs
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

            // load persisted auctions
            LoadAuctions();

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
            context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Append("Access-Control-Allow-Headers", "content-type");

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
            if (sellsToSend.TryGetValue(init.Value.idplayer, out var chaosToSend))
            {
                await SendSaleToPlayerAsync(init.Value.idplayer, new { type = "item_sold", price = chaosToSend }, ct, chaosToSend);
                sellsToSend.TryRemove(init.Value.idplayer, out _);
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
            var json = JsonSerializer.Serialize(payload);

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
            _ = SetPartyAsync(clientId, "", ct);

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

            float speed = 0.5f;        // velocidade do mob
            float aggroRange = 10f;    // distância máxima para perseguir o player
            float attackRange = 1.6f;  // distância para ataque     // dano base
            int attackCooldownMs = 2000;
            if (mob.tipo > 50)
            {
                speed = 0.3f;
                aggroRange = 500f;
                attackRange = 50f;
                if (mob.tipo == 100)
                {
                    attackCooldownMs = 3000;
                }
                else { attackCooldownMs = 1000; }
            }

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

                            if (mobs[idx].tipo == BossTipo)
                            {
                                _bossLastCombatMs["dg1"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            }
                            else if (mobs[idx].tipo == 100) // DG2
                            {
                                _bossLastCombatMs["dg2"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            }
                            if (died)
                            {

                                if (mob.tipo == BossTipo)
                                {
                                    _bossNextRespawnAtMs["dg1"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)BossRespawnDelay.TotalMilliseconds;
                                }
                                else if (mob.tipo == 100) // DG2
                                {
                                    _bossNextRespawnAtMs["dg2"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                                                  + (long)BossRespawnDelay.TotalMilliseconds;
                                }
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
                                var loot = ComputeMobLoot(mob);
                                await AwardLootAsync(clientId, loot, map, ct);
                            }
                        }
                        catch { /* ignore */ }
                        return;
                    }

                case "list_item":
                    {

                        List<object> list;
                        lock (_auctionsLock)
                        {
                            list = _auctions.Select((a, i) => new { index = i, ownerId = a.ownerId, price = a.price, item = a.item }).ToList<object>();
                        }


                        var resp = new { type = "list_item", data = list };
                        Console.WriteLine(resp);
                        await SendJsonToPlayerAsync(clientId, resp, ct);
                        return;
                    }

                case "sell_item":
                    {
                        Console.WriteLine(msg);
                        try
                        {
                            var env = JsonSerializer.Deserialize<SocketEnvelope<SellInput>>(msg);
                            Console.WriteLine("===");
                            Console.WriteLine(env);
                            Console.WriteLine("===");
                            if (env.data == null)
                            {
                                return;
                            }
                            Console.WriteLine($"Tentando vender item {env.data.item} por {env.data.price}");
                            var owner = clientId;
                            var entry = new AuctionEntry(owner, env.data.price, env.data.item, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            int index;
                            lock (_auctionsLock)
                            {
                                _auctions.Add(entry);
                                index = _auctions.Count - 1;
                                SaveAuctions();
                            }


                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"Erro ao vender item {e.Message}");
                        }
                        return;
                    }

                case "buy_item":
                    {
                        Console.WriteLine(msg);
                        try
                        {
                            var env = JsonSerializer.Deserialize<SocketEnvelope<BuyInput>>(msg);
                            if (env.data == null)
                            {
                                return;
                            }
                            Console.WriteLine($"Tentando comprar item {env.data.index} por {clientId}");
                            AuctionEntry taken;
                            lock (_auctionsLock)
                            {
                                var idx = env.data.index;
                                if (idx < 0 || idx >= _auctions.Count)
                                {

                                    return;
                                }
                                taken = _auctions[idx];
                                _auctions.RemoveAt(idx);
                                SaveAuctions();
                            }
                            var data = new { type = "item_sold", price = taken.price };
                            // deliver item to buyer (client who requested) and notify seller
                            await SendSaleToPlayerAsync(taken.ownerId, data, ct, taken.price);

                        }
                        catch
                        {
                        }
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
                    await SendToClientAsync(playerId, JsonSerializer.Serialize(leftPayload), ct);

                    // avisa os que ficaram
                    var leftToOthers = new { type = "party_info", data = new { party = prev, members = oldSet.ToArray() } };
                    var jsonLeftToOthers = JsonSerializer.Serialize(leftToOthers);
                    foreach (var m in oldSet) await SendToClientAsync(m, jsonLeftToOthers, ct);
                }
            }

            // 2) se foi só sair, encerra
            if (string.IsNullOrWhiteSpace(partyIdOrNull))
            {
                _partyOfPlayer[playerId] = null;
                return;
            }
            if (_partyMembers[partyIdOrNull].Count > 5) return;
            // 3) adicionar na nova party e avisar TODO MUNDO da party
            _partyOfPlayer[playerId] = partyIdOrNull;
            var set = _partyMembers.GetOrAdd(partyIdOrNull!, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (set) set.Add(playerId);

            var payload = new { type = "party_info", data = new { party = partyIdOrNull, members = set.ToArray() } };
            var json = JsonSerializer.Serialize(payload);
            foreach (var m in set) await SendToClientAsync(m, json, ct);
        }


        private static int ComputeMobXp(in MobData m)
        {
            // regra simples: tipo e área influenciam
            var baseXp = 10 * (m.tipo + 1) + 5 * (m.area + 1);
            return Math.Max(5, baseXp);
        }

        private static MobLoot ComputeMobLoot(MobData m)
        {
            // regra simples: tipo e área influenciam
            int currency = 0;
            int item = 0;
            while (Random.Shared.NextInt64(100) < 15)
            {
                currency++;
            }

            while (Random.Shared.NextInt64(100) < 3)
            {
                item++;
            }

            return new MobLoot(currency, item);
        }
        private static async Task SendToClientAsync(string playerId, string text, CancellationToken ct)
        {
            if (_clients.TryGetValue(playerId, out var ws) && ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
            }
        }
        private static async Task AwardLootAsync(string killerId, MobLoot mobLoot, string map, CancellationToken ct)
        {
            var partyId = _partyOfPlayer.TryGetValue(killerId, out var p) ? p : null;

            List<string> recipients;
            if (string.IsNullOrWhiteSpace(partyId))
            {
                recipients = new List<string> { killerId };
            }
            else if (_partyMembers.TryGetValue(partyId!, out var set))
            {
                recipients = set
                    .Where(pid => _clients.ContainsKey(pid) &&
                                  _playersMap.TryGetValue(pid, out var m) && m == map)
                    .ToList();

                if (recipients.Count == 0) recipients.Add(killerId);
            }
            else
            {
                recipients = new List<string> { killerId };
            }

            var count = Math.Max(1, recipients.Count);

            // quantidade total de loot
            int totalCurrency = mobLoot.currency;
            int totalItems = mobLoot.item;

            // dividir loot entre membros
            var perCurrency = totalCurrency / count;
            var remainderCurrency = totalCurrency % count;

            var perItem = totalItems / count;
            var remainderItem = totalItems % count;

            for (int i = 0; i < recipients.Count; i++)
            {


                int thisCurrency = perCurrency + (i < remainderCurrency ? 1 : 0);
                int thisItem = perItem + (i < remainderItem ? 1 : 0);
                var playerLoot = new
                {
                    type = "loot_gain",
                    who = killerId,
                    to = recipients[i],
                    loot = new
                    {
                        currency = thisCurrency,
                        item = thisItem
                    }
                };

                var lootJson = JsonSerializer.Serialize(playerLoot);
                await SendToClientAsync(recipients[i], lootJson, ct);
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
            var announceJson = JsonSerializer.Serialize(announce);

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
                        }

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
                foreach (var kv in _spawnByMap) // mapas com spawn
                {
                    var map = kv.Key;
                    var cfg = kv.Value;

                    // pegue o snapshot atual da área ANTES de qualquer branch
                    var oldAreas = _areas;
                    var oldArea = oldAreas.TryGetValue(map, out var a)
                        ? a
                        : new AreaState(map, 0, Array.Empty<MobData>());

                    // calcule os mobs novos por mapa (boss em dg1, spawner normal nos demais)
                    MobData[] newMobs;
                    if (map.Equals("dg2", StringComparison.OrdinalIgnoreCase))
                    {
                        newMobs = TickBossMap(oldArea.Mobs, stop, "dg2");
                        newMobs = UpdateMobsByAreas(newMobs, cfg, stop, true);
                    }
                    else if (map.Equals("dg1", StringComparison.OrdinalIgnoreCase))
                    {
                        newMobs = TickBossMap(oldArea.Mobs, stop, "dg1");
                        newMobs = UpdateMobsByAreas(newMobs, cfg, stop, true);
                    }
                    else
                    {
                        newMobs = UpdateMobsByAreas(oldArea.Mobs, cfg, stop); // spawner padrão
                    }

                    // diffs e early-out
                    var (adds, updates, removes, changed) = Diff(oldArea.Mobs, newMobs);
                    if (!changed) continue;

                    // swap thread-safe do estado
                    var newArea = oldArea with { Version = oldArea.Version + 1, Mobs = newMobs };
                    var newDict = new Dictionary<string, AreaState>(oldAreas, StringComparer.OrdinalIgnoreCase)
                    {
                        [map] = newArea
                    };
                    _areas = newDict;

                    // broadcast apenas para quem está no mapa
                    var deltaPayload = new
                    {
                        type = "mob_delta",
                        map = newArea.Map,
                        v = newArea.Version,
                        adds = adds.Select(ToWire).ToArray(),
                        updates,
                        removes
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(deltaPayload);
                    await BroadcastAllAsync(json, map, stop);

                    // ---------- DEBUG: Delta de mobs ----------
                    // ---------- DEBUG: Delta de mobs ----------
                    if (adds.Count > 0)
                    {
                        Console.WriteLine($"[DEBUG] Enviando delta para mapa={map} com {adds.Count} mobs novos");
                        foreach (var mob in adds)
                        {
                            Console.WriteLine($"[DEBUG] -> Mob Tipo={mob.tipo}, ID={mob.idmob}, Pos=({mob.posx},{mob.posy})");
                        }
                    }


                }
            }
        }


        private static MobData[] TickBossMap(MobData[] current, CancellationToken ct, string map)
        {
            var list = current.ToList();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            switch (map)
            {
                case "dg1":
                    {
                        var idxDG1 = list.FindIndex(m => m.tipo == BossTipo);
                        if (idxDG1 == -1)
                        {
                            if (now >= _bossNextRespawnAtMs["dg1"])
                            {
                                var boss = new MobData(
                                    Guid.NewGuid().ToString(),
                                    BossSpawnDG1.x, BossSpawnDG1.y,
                                    BossMaxLife, BossMaxLife,
                                    BossTipo, 0
                                );
                                list.Add(boss);
                                _bossLastCombatMs["dg1"] = now;
                            }
                        }
                        else
                        {
                            var b = list[idxDG1];
                            if (b.life > 0 && now - _bossLastCombatMs["dg1"] >= (long)BossOutOfCombat.TotalMilliseconds && b.life < b.maxlife)
                            {
                                var nl = Math.Min(b.maxlife, b.life + BossRegenPerTick);
                                list[idxDG1] = b with { life = nl };
                            }
                        }
                    }
                    break;
                case "dg2":
                    {
                        var idxDG2 = list.FindIndex(m => m.tipo == 100);
                        if (idxDG2 == -1)
                        {
                            if (now >= _bossNextRespawnAtMs["dg2"])
                            {
                                var madGodBoss = new MobData(
                                    Guid.NewGuid().ToString(),
                                    BossSpawnDG2.x, BossSpawnDG2.y,
                                    8000, 8000,
                                    100, 0
                                );
                                list.Add(madGodBoss);
                                _bossLastCombatMs["dg2"] = now;
                            }

                        }
                        else
                        {
                            var b2 = list[idxDG2];
                            if (b2.life > 0 && now - _bossLastCombatMs["dg2"] >= (long)BossOutOfCombat.TotalMilliseconds && b2.life < b2.maxlife)
                            {
                                var nl = Math.Min(b2.maxlife, b2.life + BossRegenPerTick);
                                list[idxDG2] = b2 with { life = nl };
                            }
                        }
                    }
                    break;
                default:
                    return list.ToArray();
            }
            // ---------- DG1 ----------


            // ---------- DG2 ----------


            return list.ToArray();
        }

        // Gera a nova lista de mobs por áreas, respeitando limites por área.
        // Gera a nova lista de mobs por áreas, respeitando limites por área.
        private static MobData[] UpdateMobsByAreas(MobData[] currentAll, SpawnData[] cfg, CancellationToken ct, bool bossMap = false)
        {
            // Agrupa os mobs atuais por área (0..cfg.Length-1)
            var byArea = currentAll
                .GroupBy(m => m.area)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (int areaIndex = 0; areaIndex < cfg.Length; areaIndex++)
            {
                if (!byArea.TryGetValue(areaIndex, out var list))
                {
                    list = new List<MobData>();
                    byArea[areaIndex] = list;
                }

                var curr = list.Count;
                if (curr >= MaxMobsPerArea || bossMap)
                {
                    // mover mobs (AI simples) em direção a players do MESMO MAPA
                    for (int i = 0; i < list.Count; i++)
                    {
                        var playersInArea = _players.Values
                            .Where(p => _playersMap.TryGetValue(p.idplayer, out var m) && m == cfg[areaIndex].Mapa)
                            .ToList();

                        list[i] = MoveMobAI(list[i], playersInArea, ct);
                    }
                    continue;
                }

                var toSpawn = Math.Min(MaxMobsPerArea - curr, MaxPerTickPerArea);
                var sCfg = cfg[areaIndex];

                for (int i = 0; i < toSpawn; i++)
                {
                    int x = sCfg.PosX >= 0 ? Random.Shared.Next(0, (int)sCfg.PosX + 1) : Random.Shared.Next((int)sCfg.PosX, 1);
                    int y = sCfg.PosY >= 0 ? Random.Shared.Next(0, (int)sCfg.PosY + 1) : Random.Shared.Next((int)sCfg.PosY, 1);

                    var mob = new MobData(
                        Guid.NewGuid().ToString(),
                        x, y,
                        100 * (areaIndex + 1),        // life/base simples (mude à vontade por DG)
                        100 * (areaIndex + 1),
                        Random.Shared.Next(4),        // tipo aleatório (ou fixe por DG)
                        areaIndex                     // área local (0 nas DGs)
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
