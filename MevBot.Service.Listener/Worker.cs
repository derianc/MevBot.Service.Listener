using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using StackExchange.Redis;

namespace MevBot.Service.Listener
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly ConnectionMultiplexer _redis;
        private readonly IDatabase _redisDb;

        private readonly string _wsUrl;
        // private readonly string _splTokenAddress;
        private readonly string _redisAnalyzeQueue = "solana_analyze_queue";
        private readonly string _redisConnectionString;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            _wsUrl = _configuration.GetValue<string>("Solana:WsUrl") ?? string.Empty;
            // _splTokenAddress = _configuration.GetValue<string>("Solana:SPL_TOKEN_ADDRESS") ?? string.Empty;
            _redisConnectionString = _configuration.GetValue<string>("Redis:REDIS_URL") ?? string.Empty;

            // connect to redis
            var options = ConfigurationOptions.Parse(_redisConnectionString);
            // options.AbortOnConnectFail = false; // Prevents Redis from failing on first attempt

            _redis = ConnectionMultiplexer.Connect(options);
            _redisDb = _redis.GetDatabase();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int retryDelaySeconds = 60;

            _logger.LogInformation("{time} - Starting Solana MEV Bot Listener", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Create and connect WebSocket
                    using (ClientWebSocket ws = new ClientWebSocket())
                    {
                        _logger.LogInformation("{time} - Connecting to WebSocket: {wsUrl}", DateTimeOffset.Now, _wsUrl);
                        await ws.ConnectAsync(new Uri(_wsUrl), stoppingToken);
                        _logger.LogInformation("{time} - Connected to WebSocket", DateTimeOffset.Now);

                        // Prepare and send the subscription message.
                        var subscribeMessage = new
                        {
                            jsonrpc = "2.0",
                            id = 1,
                            method = "logsSubscribe",
                            @params = new object[]
                            {
                                // Uncomment and update filter as needed.
                                // new { mentions = new string[] { _splTokenAddress } },
                                "all",
                                new { commitment = "confirmed" }
                            }
                        };

                        string messageJson = JsonSerializer.Serialize(subscribeMessage);
                        var messageBytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(messageJson));
                        await ws.SendAsync(messageBytes, WebSocketMessageType.Text, true, stoppingToken);
                        _logger.LogInformation("{time} - Subscription message sent. Listening for log events", DateTimeOffset.Now);

                        var buffer = new byte[4096];

                        // Process messages until the connection is closed.
                        while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                        {
                            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                _logger.LogWarning("{time} - WebSocket closed by remote party", DateTimeOffset.Now);
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", stoppingToken);
                                break;
                            }
                            else if (result.MessageType == WebSocketMessageType.Text)
                            {
                                string response = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                try
                                {
                                    // Push the message to the Redis analyze queue.
                                    await _redisDb.ListLeftPushAsync(_redisAnalyzeQueue, response);
                                    _logger.LogInformation("{time} - Pushed message to Redis queue: {queueName}", DateTimeOffset.Now, _redisAnalyzeQueue);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError("Error pushing message to Redis: {Message}", ex.Message);
                                }
                            }
                        }
                    }
                }
                catch (WebSocketException ex)
                {
                    _logger.LogError("{time} - WebSocket error: {Error}. Restarting connection in {Delay} seconds...", DateTimeOffset.Now, ex.Message, retryDelaySeconds);
                }
                catch (Exception ex)
                {
                    _logger.LogError("{time} - Unexpected error: {Error}. Restarting connection in {Delay} seconds...", DateTimeOffset.Now, ex.Message, retryDelaySeconds);
                }

                // Wait before retrying the connection.
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), stoppingToken);
            }
        }
    }
}
