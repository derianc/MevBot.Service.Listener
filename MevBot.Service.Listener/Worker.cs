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
        private readonly string _splTokenAddress;
        private readonly string _redisQueueName = "solana_logs_queue";
        private readonly string _redisConnectionString;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            _wsUrl = _configuration.GetValue<string>("Solana:WsUrl") ?? string.Empty;
            _splTokenAddress = _configuration.GetValue<string>("Solana:SPL_TOKEN_ADDRESS") ?? string.Empty;
            _redisConnectionString = _configuration.GetValue<string>("Redis:REDIS_URL") ?? string.Empty;

            // connect to redis
            var options = ConfigurationOptions.Parse(_redisConnectionString);
            // options.AbortOnConnectFail = false; // Prevents Redis from failing on first attempt

            _redis = ConnectionMultiplexer.Connect(options);
            _redisDb = _redis.GetDatabase();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("{time} - Starting Solana MEV Bot with SPL Token filter: ", DateTimeOffset.Now);

                using (ClientWebSocket ws = new ClientWebSocket())
                {
                    await ws.ConnectAsync(new Uri(_wsUrl), CancellationToken.None);
                    _logger.LogInformation("{time} - Connected to Solana WebSocket", DateTimeOffset.Now);

                    // Prepare and send the subscription message.
                    // This subscribes to log messages filtered by the specified SPL token address.
                    var subscribeMessage = new
                    {
                        jsonrpc = "2.0",
                        id = 1,
                        method = "logsSubscribe",
                        @params = new object[]
                        {
                        // Filter logs that mention the SPL token address.
                        new { mentions = new string[] { _splTokenAddress } },
                        //"all",
                        new { commitment = "confirmed" }
                        }
                    };

                    string messageJson = JsonSerializer.Serialize(subscribeMessage);
                    var messageBytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(messageJson));
                    await ws.SendAsync(messageBytes, WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogInformation("{time} - Subscription message sent. Listening for log events", DateTimeOffset.Now);

                    // Buffer for receiving messages.
                    var buffer = new byte[4096];

                    //// Push test message to Redis
                    //await _redisDb.ListLeftPushAsync(_redisQueueName, "Test message");
                    //_logger.LogInformation("{time} - Message pushed to Redis queue", DateTimeOffset.Now);

                    // Continuously receive messages.
                    while (ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            _logger.LogInformation("{time} - Websocket Closed", DateTimeOffset.Now);
                            break;
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            // Convert the received bytes into a string.
                            string response = Encoding.UTF8.GetString(buffer, 0, result.Count);

                            try
                            {
                                // Deserialize the JSON response into our LogsNotificationResponse object.
                                //var logsNotification = JsonSerializer.Deserialize<LogsNotificationResponse>(response);
                                //_logger.LogInformation("{time} - Received JSON: " + logsNotification, DateTimeOffset.Now);

                                // push message to redis
                                await _redisDb.ListLeftPushAsync(_redisQueueName, response);
                                _logger.LogInformation("{time} - Pushed logsNotification to Redis queue: {queueName}", DateTimeOffset.Now, _redisQueueName);
                            }
                            catch (JsonException ex)
                            {
                                Console.WriteLine("JSON Deserialization error: " + ex.Message);
                            }
                        }
                    }
                }
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
