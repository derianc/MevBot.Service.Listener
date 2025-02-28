using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class SolanaWebSocketClient
{
    private readonly Uri _solanaWebSocketUri;
    private readonly ClientWebSocket _clientWebSocket;
    private readonly Action<string> _messageHandler;

    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1); // Prevent concurrent sends

    public SolanaWebSocketClient(string solanaWebSocketUrl, Action<string> messageHandler)
    {
        _solanaWebSocketUri = new Uri(solanaWebSocketUrl);
        _clientWebSocket = new ClientWebSocket();
        _messageHandler = messageHandler;
    }

    public async Task ConnectAsync()
    {
        try
        {
            await _clientWebSocket.ConnectAsync(_solanaWebSocketUri, CancellationToken.None);
            Console.WriteLine("Connected to Solana WebSocket.");

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenAsync(_cts.Token)); // Run ListenAsync in background
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket Connection Error: {ex.Message}");
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096]; // 4KB buffer per chunk
        var messageBuilder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested && _clientWebSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await _clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                string receivedChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                messageBuilder.Append(receivedChunk);
            } while (!result.EndOfMessage);

            string completeMessage = messageBuilder.ToString();
            messageBuilder.Clear();

            _messageHandler.Invoke(completeMessage);
        }
    }

    public async Task SendAsync(object message, CancellationToken cancellationToken = default)
    {
        if (_clientWebSocket.State != WebSocketState.Open)
        {
            Console.WriteLine("WebSocket is not connected.");
            return;
        }

        string messageJson = JsonSerializer.Serialize(message);
        var messageBytes = Encoding.UTF8.GetBytes(messageJson);
        var messageSegment = new ArraySegment<byte>(messageBytes);

        await _sendLock.WaitAsync(); // Prevent multiple sends at once
        try
        {
            await _clientWebSocket.SendAsync(messageSegment, WebSocketMessageType.Text, true, cancellationToken);
            Console.WriteLine($"Sent: {messageJson}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        Console.WriteLine("Disconnected from Solana WebSocket.");
    }
}
