using MevBot.Service.Redis;
using System.Linq;

namespace MevBot.Service.Listener
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _wsUrl;
        private readonly string _redisAnalyzeQueue = "solana_analyze_queue";
        private readonly string _redisConnectionString;
        private readonly RedisPublisher _redisPublisher;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _wsUrl = _configuration.GetValue<string>("Solana:WsUrl") ?? string.Empty;
            _redisConnectionString = _configuration.GetValue<string>("Redis:REDIS_URL") ?? string.Empty;
            _redisPublisher = new RedisPublisher(_redisConnectionString);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Continuously check for token addresses until provided
            string tokenAddressesConfig = _configuration.GetValue<string>("Solana:SPL_TOKEN_ADDRESS") ?? string.Empty;
            while (string.IsNullOrWhiteSpace(tokenAddressesConfig))
            {
                _logger.LogError("No SPL token addresses provided in the configuration. Sleeping for 10 minutes before checking again.");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                tokenAddressesConfig = _configuration.GetValue<string>("Solana:SPL_TOKEN_ADDRESS") ?? string.Empty;
            }

            // Split and trim the token addresses from the configuration
            var splTokenAddresses = tokenAddressesConfig
                                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(token => token.Trim())
                                        .ToArray();

            // Define the SolanaWebSocketClient with a message handler that filters based on token addresses
            var solanaClient = new SolanaWebSocketClient(_wsUrl, async (message) =>
            {
                // Filter the message before publishing: check if it contains any of the token addresses
                if (!splTokenAddresses.Any(token => message.Contains(token)))
                {
                    _logger.LogInformation($"{DateTime.UtcNow}: Filtered out message.");
                    return;
                }

                _logger.LogInformation($"{DateTime.UtcNow}: Received message: {message}");
                await _redisPublisher.PublishMessageAsync(message, _redisAnalyzeQueue);
            });

            await solanaClient.ConnectAsync();

            // Create a separate subscription for each token address.
            int subscriptionId = 1;
            foreach (var tokenAddress in splTokenAddresses)
            {
                var subscribeMessage = new
                {
                    jsonrpc = "2.0",
                    id = subscriptionId++,
                    method = "logsSubscribe",
                    @params = new object[]
                    {
                        new { mentions = new string[] { tokenAddress } },
                        new { commitment = "confirmed" }
                    }
                };

                await solanaClient.SendAsync(subscribeMessage);
            }

            // Keep the service running until cancellation is requested.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
