using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServidorLocal
{
    public record PlayerData(string idplayer, float posx, float posy);

    public class Program
    {
        public static ConcurrentDictionary<string, WebSocket> connectedClients = new();
        public static ConcurrentDictionary<string, PlayerData> players = new();

        public static event Action<string>? OnPlayerConnected;
        public static event Action<string>? OnPlayerDisconnected;

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://0.0.0.0:5260");
            var app = builder.Build();

            app.UseWebSockets();

            app.Map("/ws", async context =>
            {
                Console.WriteLine("Nova tentativa de conexão recebida...");

                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    Console.WriteLine("Falha: não é uma requisição WebSocket");
                    return;
                }

                WebSocket webSocket = null;

                try
                {
                    webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    Console.WriteLine("WebSocket aceito pelo servidor");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro ao aceitar WebSocket: {ex.Message}");
                    return;
                }

                var buffer = new byte[1024];
                WebSocketReceiveResult result = null;
                string initialMsg = "";

                try
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), default);
                    initialMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"Mensagem inicial recebida do cliente: '{initialMsg}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro recebendo mensagem inicial: {ex.Message}");
                    return;
                }

                string clientId = string.IsNullOrEmpty(initialMsg) ? Guid.NewGuid().ToString() : initialMsg;

                // envia UUID de volta
                try
                {
                    var idBytes = Encoding.UTF8.GetBytes(clientId);
                    await webSocket.SendAsync(idBytes, WebSocketMessageType.Text, true, default);
                    Console.WriteLine($"UUID enviado para o cliente: {clientId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro enviando UUID: {ex.Message}");
                    return;
                }

                connectedClients[clientId] = webSocket;

                OnPlayerConnected?.Invoke(clientId);
                _ = BroadcastPlayerConnected(clientId);

                Console.WriteLine($"Cliente conectado com sucesso: {clientId}");

                await HandleClient(webSocket, clientId);
            });

            OnPlayerConnected += (uuid) => Console.WriteLine($"[Evento] Player conectado: {uuid}");
            OnPlayerDisconnected += (uuid) => Console.WriteLine($"[Evento] Player desconectado: {uuid}");

            app.Run();
        }

        private static async Task HandleClient(WebSocket socket, string clientId)
        {
            var buffer = new byte[1024];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), default);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechando", default);
                        connectedClients.TryRemove(clientId, out _);
                        players.TryRemove(clientId, out _);

                        OnPlayerDisconnected?.Invoke(clientId);
                        _ = BroadcastPlayerDisconnected(clientId);

                        Console.WriteLine($"Cliente desconectado: {clientId}");
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        PlayerData data = JsonSerializer.Deserialize<PlayerData>(msg)!;

                        data = data with { idplayer = clientId };
                        players[clientId] = data;

                        string allPlayersJson = JsonSerializer.Serialize(players.Values);
                        byte[] bytes = Encoding.UTF8.GetBytes(allPlayersJson);

                        foreach (var kvp in connectedClients)
                        {
                            if (kvp.Value.State == WebSocketState.Open)
                                await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, default);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro cliente {clientId}: {ex.Message}");
                connectedClients.TryRemove(clientId, out _);
                players.TryRemove(clientId, out _);

                OnPlayerDisconnected?.Invoke(clientId);
                _ = BroadcastPlayerDisconnected(clientId);
            }
        }

        private static async Task BroadcastPlayerConnected(string clientId)
        {
            var message = JsonSerializer.Serialize(new { type = "connect", idplayer = clientId });
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            foreach (var kvp in connectedClients)
            {
                if (kvp.Value.State == WebSocketState.Open)
                    await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, default);
            }
        }

        private static async Task BroadcastPlayerDisconnected(string clientId)
        {
            var message = JsonSerializer.Serialize(new { type = "disconnect", idplayer = clientId });
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            foreach (var kvp in connectedClients)
            {
                if (kvp.Value.State == WebSocketState.Open)
                    await kvp.Value.SendAsync(bytes, WebSocketMessageType.Text, true, default);
            }
        }
    }
}
