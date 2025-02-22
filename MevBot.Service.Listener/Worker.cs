using MevBot.Service.Redis;

namespace MevBot.Service.Listener
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;

        private readonly string _wsUrl;
        // private readonly string _splTokenAddress;
        private readonly string _redisAnalyzeQueue = "solana_analyze_queue";
        private readonly string _redisConnectionString;

        private readonly RedisPublisher _redisPublisher;

        public Worker(ILogger<Worker> logger, IConfiguration configuration) 
        {
            _logger = logger;
            _configuration = configuration;

            _wsUrl = _configuration.GetValue<string>("Solana:WsUrl") ?? string.Empty;
            _redisConnectionString = _configuration.GetValue<string>("Redis:REDIS_URL") ?? string.Empty;
            // _splTokenAddress = _configuration.GetValue<string>("Solana:SPL_TOKEN_ADDRESS") ?? string.Empty;

            _redisPublisher = new RedisPublisher(_redisConnectionString);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // define the SolanaWebSocketClient
            var solanaClient = new SolanaWebSocketClient(_wsUrl, async (message) =>
            {
                // publish the message to the Redis channel
                await _redisPublisher.PublishMessageAsync(message, _redisAnalyzeQueue);
            });

            // connect and subscribe to the Solana WebSocket
            var subscribeMessage = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "logsSubscribe",
                @params = new object[]
                {
                    // new { mentions = new string[] { _splTokenAddress } },
                    "all",
                    new { commitment = "confirmed" }
                }
            };
            await solanaClient.ConnectAsync();
            await solanaClient.SendAsync(subscribeMessage);
        }
    }
}
