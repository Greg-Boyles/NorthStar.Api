using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;

namespace NorthStar.Api.Services;

/// <summary>
/// Background service that orchestrates VehicleStreamManagers for all active VINs.
/// Handles discovery, cleanup, lock renewal, and token refresh.
/// </summary>
public class VehicleStreamService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VehicleStreamService> _logger;
    private readonly ConcurrentDictionary<string, VehicleStreamManager> _activeStreams = new();
    
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockRenewalInterval = TimeSpan.FromMinutes(5);

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
        foreach (var manager in _activeStreams.Values)
        {
            await manager.StopStreamsAsync();
            manager.Dispose();
        }

        _logger.LogInformation("VehicleStreamService stopped");
    }

    private async Task MonitorAndManageStreamsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
        var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();

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

        // Cleanup stale streams and renew locks
        var vinsToRemove = new List<string>();
        foreach (var (vin, manager) in _activeStreams)
        {
            var lastAccess = await cache.GetLastAccessAsync(vin, ct);
            if (lastAccess == null || DateTimeOffset.UtcNow - lastAccess.Value > StaleThreshold)
            {
                _logger.LogInformation("VIN {Vin} is stale (last access: {LastAccess}), stopping streams", vin, lastAccess);
                vinsToRemove.Add(vin);
            }
            else
            {
                // Renew distributed lock periodically
                if (DateTimeOffset.UtcNow - manager.LastLockRenewal > LockRenewalInterval)
                {
                    var lockKey = $"stream_lock:{vin}";
                    var renewed = await lockService.RenewLockAsync(lockKey, manager.LockValue, LockExpiry);
                    
                    if (!renewed)
                    {
                        _logger.LogWarning("Failed to renew lock for VIN {Vin}, stopping streams", vin);
                        vinsToRemove.Add(vin);
                        continue;
                    }
                    manager.LastLockRenewal = DateTimeOffset.UtcNow;
                }

                // Refresh access token
                if (DateTimeOffset.UtcNow - manager.LastTokenRefresh > TokenRefreshInterval)
                {
                    await RefreshAccessTokenAsync(vin, ct);
                }
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
            var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();
            var authService = scope.ServiceProvider.GetRequiredService<PolestarAuthService>();
            var snapshotService = scope.ServiceProvider.GetRequiredService<VehicleSnapshotService>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

            // Acquire distributed lock to ensure only one task streams this VIN
            var lockKey = $"stream_lock:{vin}";
            var lockValue = Guid.NewGuid().ToString();
            var lockAcquired = await lockService.AcquireLockAsync(lockKey, lockValue, LockExpiry);

            if (!lockAcquired)
            {
                _logger.LogInformation("Another task is already streaming VIN {Vin}, skipping", vin);
                return;
            }

            _logger.LogInformation("Acquired distributed lock for VIN {Vin}", vin);

            // Get initial access token
            var tokenResponse = await authService.RefreshTokenAsync(refreshToken);
            var accessToken = tokenResponse.AccessToken;

            // Seed cache with an initial snapshot so data is available immediately
            try
            {
                _logger.LogInformation("Seeding cache with initial snapshot for VIN {Vin}", vin);
                await snapshotService.GetSnapshotAsync(accessToken, vin, ct);
                _logger.LogInformation("Cache seeded for VIN {Vin}", vin);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed cache for VIN {Vin}, streams will populate it", vin);
            }

            // Create and start VehicleStreamManager
            var manager = new VehicleStreamManager(
                vin,
                lockValue,
                _serviceProvider,
                loggerFactory.CreateLogger<VehicleStreamManager>());

            await manager.StartStreamsAsync(accessToken);
            _activeStreams[vin] = manager;

            _logger.LogInformation("Streams started for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start streams for VIN {Vin}", vin);
        }
    }

    private async Task StopStreamsForVinAsync(string vin, CancellationToken ct)
    {
        if (_activeStreams.TryRemove(vin, out var manager))
        {
            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
            var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();

            // Stop streams
            await manager.StopStreamsAsync();

            // Release distributed lock
            var lockKey = $"stream_lock:{vin}";
            await lockService.ReleaseLockAsync(lockKey, manager.LockValue);

            // Cleanup cache and dispose manager
            await cache.RemoveAsync(vin, ct);
            manager.Dispose();

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

            // Refresh to keep the token alive (Polestar may rotate refresh tokens)
            var tokenResponse = await authService.RefreshTokenAsync(refreshToken);
            
            // Update stored refresh token if rotated
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                await cache.SetRefreshTokenAsync(vin, tokenResponse.RefreshToken, ct);
            }

            if (_activeStreams.TryGetValue(vin, out var manager))
            {
                manager.LastTokenRefresh = DateTimeOffset.UtcNow;
            }

            _logger.LogDebug("Refreshed token for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh token for VIN {Vin}, stopping streams", vin);
            await StopStreamsForVinAsync(vin, ct);
        }
    }
}
