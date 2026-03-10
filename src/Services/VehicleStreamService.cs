using Microsoft.Extensions.Caching.Distributed;

namespace NorthStar.Api.Services;

/// <summary>
/// Background service that manages persistent gRPC streams for active VINs.
/// Streams are started on-demand when POST /api/stream/{vin}/start is called.
/// Auto-cleanup stops streams if lastAccess is stale (>30 min).
/// </summary>
public class VehicleStreamService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VehicleStreamService> _logger;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);

    public VehicleStreamService(
        IServiceProvider serviceProvider,
        ILogger<VehicleStreamService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VehicleStreamService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
                await PerformCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in VehicleStreamService cleanup cycle");
            }
        }

        _logger.LogInformation("VehicleStreamService stopped");
    }

    private Task PerformCleanupAsync(CancellationToken ct)
    {
        // TODO: In a real implementation, we need a way to enumerate all active VINs
        // For now, this is a placeholder. We'll need to either:
        // 1. Keep an in-memory set of active VINs, or
        // 2. Use Redis SCAN command to find all tokens:{vin}:lastAccess keys
        
        // This will be implemented in the next iteration once we have the actual
        // streaming infrastructure in place
        
        _logger.LogDebug("Cleanup cycle running (placeholder - full implementation pending)");
        return Task.CompletedTask;
    }

    // TODO: Methods to implement in next iteration:
    // - StartStreamsForVin(string vin, string refreshToken) - opens 8 gRPC streams
    // - StopStreamsForVin(string vin) - closes streams and removes from tracking
    // - RefreshAccessToken(string vin) - uses stored refresh token to get new access token
    // - HandleStreamMessage<T>(string vin, T message) - writes stream updates to cache
}
