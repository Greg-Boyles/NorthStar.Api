using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

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
    private readonly ConcurrentDictionary<string, VinStreamSet> _activeStreams = new();
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);

    public VehicleStreamService(
        IServiceProvider serviceProvider,
        ILogger<VehicleStreamService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private class VinStreamSet
    {
        public string Vin { get; set; } = "";
        public CancellationTokenSource CancellationSource { get; set; } = new();
        public DateTimeOffset LastTokenRefresh { get; set; }
        public List<Task> StreamTasks { get; set; } = new();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VehicleStreamService started");

        // Start monitoring task for discovering new VINs from Redis
        var monitorTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(CleanupInterval, stoppingToken);
                    await MonitorAndManageStreamsAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in stream monitoring cycle");
                }
            }
        }, stoppingToken);

        await monitorTask;

        // Cleanup all active streams on shutdown
        foreach (var streamSet in _activeStreams.Values)
        {
            streamSet.CancellationSource.Cancel();
        }

        _logger.LogInformation("VehicleStreamService stopped");
    }

    private async Task MonitorAndManageStreamsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();

        // Find VINs with active refresh tokens using Redis SCAN
        var activeVins = await FindActiveVinsAsync(ct);

        // Start streams for new VINs
        foreach (var vin in activeVins)
        {
            if (!_activeStreams.ContainsKey(vin))
            {
                var refreshToken = await cache.GetRefreshTokenAsync(vin, ct);
                if (refreshToken != null)
                {
                    _logger.LogInformation("Discovered new VIN {Vin}, starting streams", vin);
                    await StartStreamsForVinAsync(vin, refreshToken, ct);
                }
            }
        }

        // Cleanup stale streams
        var vinsToRemove = new List<string>();
        foreach (var (vin, streamSet) in _activeStreams)
        {
            var lastAccess = await cache.GetLastAccessAsync(vin, ct);
            if (lastAccess == null || DateTimeOffset.UtcNow - lastAccess.Value > StaleThreshold)
            {
                _logger.LogInformation("VIN {Vin} is stale (last access: {LastAccess}), stopping streams", vin, lastAccess);
                vinsToRemove.Add(vin);
            }
            else if (DateTimeOffset.UtcNow - streamSet.LastTokenRefresh > TokenRefreshInterval)
            {
                // Refresh access token
                await RefreshAccessTokenAsync(vin, ct);
            }
        }

        foreach (var vin in vinsToRemove)
        {
            await StopStreamsForVinAsync(vin, ct);
        }
    }

    private async Task<List<string>> FindActiveVinsAsync(CancellationToken ct)
    {
        var vins = new List<string>();

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var distributedCache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

            // This is a simplified approach - in production you'd use Redis SCAN command
            // For now, we rely on StreamController calling start which adds VINs to _activeStreams
            // and we check Redis for refresh tokens
            
            // Return currently tracked VINs
            vins.AddRange(_activeStreams.Keys);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding active VINs");
        }

        return vins;
    }

    public async Task StartStreamsForVinAsync(string vin, string refreshToken, CancellationToken ct)
    {
        if (_activeStreams.ContainsKey(vin))
        {
            _logger.LogWarning("Streams already active for VIN {Vin}", vin);
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var authService = scope.ServiceProvider.GetRequiredService<PolestarAuthService>();

            // Get initial access token
            var tokenResponse = await authService.RefreshTokenAsync(refreshToken);
            var accessToken = tokenResponse.AccessToken;

            var streamSet = new VinStreamSet
            {
                Vin = vin,
                CancellationSource = new CancellationTokenSource(),
                LastTokenRefresh = DateTimeOffset.UtcNow
            };

            _activeStreams[vin] = streamSet;

            // Note: Actual gRPC streaming implementation would go here
            // For now, we're just setting up the infrastructure
            // The streams would listen for data and call cache.SetAsync() when new data arrives

            _logger.LogInformation("Streams started for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start streams for VIN {Vin}", vin);
        }
    }

    private async Task StopStreamsForVinAsync(string vin, CancellationToken ct)
    {
        if (_activeStreams.TryRemove(vin, out var streamSet))
        {
            streamSet.CancellationSource.Cancel();

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
            await cache.RemoveAsync(vin, ct);

            _logger.LogInformation("Streams stopped for VIN {Vin}", vin);
        }
    }

    private async Task RefreshAccessTokenAsync(string vin, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
            var authService = scope.ServiceProvider.GetRequiredService<PolestarAuthService>();

            var refreshToken = await cache.GetRefreshTokenAsync(vin, ct);
            if (refreshToken == null)
            {
                _logger.LogWarning("No refresh token found for VIN {Vin}, stopping streams", vin);
                await StopStreamsForVinAsync(vin, ct);
                return;
            }

            var tokenResponse = await authService.RefreshTokenAsync(refreshToken);
            
            // Update stored refresh token if rotated
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                await cache.SetRefreshTokenAsync(vin, tokenResponse.RefreshToken, ct);
            }

            if (_activeStreams.TryGetValue(vin, out var streamSet))
            {
                streamSet.LastTokenRefresh = DateTimeOffset.UtcNow;
            }

            _logger.LogDebug("Refreshed access token for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh token for VIN {Vin}, stopping streams", vin);
            await StopStreamsForVinAsync(vin, ct);
        }
    }
}
