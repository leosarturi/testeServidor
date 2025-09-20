using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using ServidorLocal.Domain;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ServidorLocal
{
    public class Program
    {
        // -------------------- Estado --------------------
        private static readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private static readonly ConcurrentDictionary<string, PlayerData> _players = new();
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        // eventos (opcionais)
        public static event Action<string>? OnPlayerConnected;
        public static event Action<string>? OnPlayerDisconnected;

        // -------------------- Startup --------------------
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://0.0.0.0:443");

            var app = builder.Build();
            app.UseWebSockets();

            // logs básicos de eventos
            OnPlayerConnected += id => Console.WriteLine($"[Evento] Player conectado: {id}");
            OnPlayerDisconnected += id => Console.WriteLine($"[Evento] Player desconectado: {id}");

            // endpoint websocket
            app.Map("/ws", HandleWebSocketAsync);

            app.Run();
        }

        // -------------------- Roteamento principal --------------------
        private static async Task HandleWebSocketAsync(HttpContext context)
        {
            Console.WriteLine("Nova tentativa de conexão recebida...");

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
                Console.WriteLine("WebSocket aceito pelo servidor");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao aceitar WebSocket: {ex.Message}");
                return;
            }

            var ct = context.RequestAborted;

            // 1) obter clientId (primeira mensagem) ou gerar GUID
            var clientId = await ReceiveFirstMessageAsClientIdAsync(socket, ct);
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = Guid.NewGuid().ToString();

            // 2) enviar de volta o UUID
            await SendClientIdAsync(socket, clientId, ct);

            // 3) registrar cliente + avisar rede
            RegisterClient(clientId, socket);
            _ = BroadcastPlayerConnectedAsync(clientId, ct);

            Console.WriteLine($"Cliente conectado com sucesso: {clientId}");

            // 4) loop do cliente
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
                Console.WriteLine($"Mensagem inicial recebida do cliente: '{initialMsg}'");
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
                Console.WriteLine($"UUID enviado para o cliente: {clientId}");
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
            catch { /* ignore */ }

            _clients.TryRemove(clientId, out _);
            _players.TryRemove(clientId, out _);

            OnPlayerDisconnected?.Invoke(clientId);
            _ = BroadcastPlayerDisconnectedAsync(clientId, ct);

            Console.WriteLine($"Cliente desconectado: {clientId}");
        }

        // -------------------- Loop de mensagens --------------------
        private static async Task HandleClientLoopAsync(WebSocket socket, string clientId, CancellationToken ct)
        {
            var buffer = new byte[4096];

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    // suporta mensagens fragmentadas
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
            // espera PlayerData; se vier sem idplayer, sobrescrevemos
            PlayerData data;
            try
            {
                var incoming = JsonSerializer.Deserialize<PlayerData?>(msg, _json);
                data = incoming.HasValue
                    ? incoming.Value with { idplayer = clientId }
                    : new PlayerData(clientId, 0, 0);
            }
            catch
            {
                // mensagem inválida -> ignora
                return;
            }

            _players[clientId] = data;
            await BroadcastAllPlayersAsync(ct);
        }

        // -------------------- Broadcasts --------------------
        private static async Task BroadcastAllPlayersAsync(CancellationToken ct)
        {
            try
            {
                var allPlayersJson = JsonSerializer.Serialize(_players.Values, _json);
                var bytes = Encoding.UTF8.GetBytes(allPlayersJson);

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
            await BroadcastRawAsync(message, ct);
        }

        private static async Task BroadcastPlayerDisconnectedAsync(string clientId, CancellationToken ct)
        {
            var message = JsonSerializer.Serialize(new { type = "disconnect", idplayer = clientId }, _json);
            await BroadcastRawAsync(message, ct);
        }

        private static async Task BroadcastRawAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            foreach (var kvp in _clients)
            {
                if (kvp.Value.State == WebSocketState.Open)
                    await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
    }
}
