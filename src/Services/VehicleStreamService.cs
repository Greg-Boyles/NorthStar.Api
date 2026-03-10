using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using OdometerProtos = NorthStar.Api.Protos;
using ExteriorProtos = NorthStar.Api.Protos.Exterior;
using AvailabilityProtos = NorthStar.Api.Protos.Availability;
using ClimateProtos = NorthStar.Api.Protos.ParkingClimatization;
using ChronosProtos = NorthStar.Api.Protos.Chronos;
using NorthStar.Api.Models;

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
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";
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

    private class VinStreamSet
    {
        public string Vin { get; set; } = "";
        public CancellationTokenSource CancellationSource { get; set; } = new();
        public DateTimeOffset LastTokenRefresh { get; set; }
        public DateTimeOffset LastLockRenewal { get; set; }
        public string LockValue { get; set; } = "";
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

        // Cleanup stale streams and renew locks
        var vinsToRemove = new List<string>();
        foreach (var (vin, streamSet) in _activeStreams)
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
                if (DateTimeOffset.UtcNow - streamSet.LastLockRenewal > LockRenewalInterval)
                {
                    var renewed = await RenewStreamLockAsync(vin, streamSet.LockValue, ct);
                    if (!renewed)
                    {
                        _logger.LogWarning("Failed to renew lock for VIN {Vin}, stopping streams", vin);
                        vinsToRemove.Add(vin);
                        continue;
                    }
                    streamSet.LastLockRenewal = DateTimeOffset.UtcNow;
                }

                // Refresh access token
                if (DateTimeOffset.UtcNow - streamSet.LastTokenRefresh > TokenRefreshInterval)
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
            var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var authService = scope.ServiceProvider.GetRequiredService<PolestarAuthService>();

            // Acquire distributed lock to ensure only one task streams this VIN
            var lockKey = $"stream_lock:{vin}";
            var lockValue = Guid.NewGuid().ToString();
            var db = redis.GetDatabase();
            var lockAcquired = await db.StringSetAsync(lockKey, lockValue, LockExpiry, When.NotExists);

            if (!lockAcquired)
            {
                _logger.LogInformation("Another task is already streaming VIN {Vin}, skipping", vin);
                return;
            }

            _logger.LogInformation("Acquired distributed lock for VIN {Vin}", vin);

            // Get initial access token
            var tokenResponse = await authService.RefreshTokenAsync(refreshToken);
            var accessToken = tokenResponse.AccessToken;

            var streamSet = new VinStreamSet
            {
                Vin = vin,
                CancellationSource = new CancellationTokenSource(),
                LastTokenRefresh = DateTimeOffset.UtcNow,
                LastLockRenewal = DateTimeOffset.UtcNow,
                LockValue = lockValue
            };

            _activeStreams[vin] = streamSet;

            // Create gRPC channel
            var channel = GrpcChannel.ForAddress(C3Host);
            var headers = new Metadata
            {
                { "authorization", $"Bearer {accessToken}" },
                { "vin", vin }
            };

            // Start 8 concurrent gRPC streams
            var streamCts = streamSet.CancellationSource.Token;
            streamSet.StreamTasks = new List<Task>
            {
                StreamBatteryAsync(channel, headers, vin, streamCts),
                StreamOdometerAsync(channel, headers, vin, streamCts),
                StreamExteriorAsync(channel, headers, vin, streamCts),
                StreamAvailabilityAsync(channel, headers, vin, streamCts)
                // TODO: Add remaining 4 streams:
                // - ParkingClimatization
                // - GlobalChargeTimer (Chronos)
                // - ClimateTimer (Chronos)
                // - ClimateTimerSettings (Chronos)
            };

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
            var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();

            // Release distributed lock
            await ReleaseStreamLockAsync(vin, streamSet.LockValue, ct);

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

    private async Task StreamBatteryAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        try
        {
            var client = new BatteryProtos.BatteryService.BatteryServiceClient(channel);
            var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
            var call = client.GetBattery(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Battery != null)
                {
                    _logger.LogDebug("Battery update for VIN {Vin}: {Level}%", vin, response.Battery.BatteryChargeLevelPercentage);
                    
                    // Update cache with battery data
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    var tripService = scope.ServiceProvider.GetRequiredService<PolestarTripService>();
                    
                    var snapshot = await cache.GetSnapshotAsync(vin, ct) ?? new VehicleSnapshot { Vin = vin };
                    snapshot.Battery = tripService.MapBatteryData(vin, response.Battery);
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Battery stream cancelled for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Battery stream error for VIN {Vin}", vin);
        }
    }

    private async Task StreamOdometerAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        try
        {
            var client = new OdometerProtos.OdometerService.OdometerServiceClient(channel);
            var request = new OdometerProtos.GetOdometerRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
            var call = client.GetOdometer(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Odometer != null)
                {
                    _logger.LogDebug("Odometer update for VIN {Vin}: {Meters}m", vin, response.Odometer.OdometerMeters);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    var tripService = scope.ServiceProvider.GetRequiredService<PolestarTripService>();
                    
                    var snapshot = await cache.GetSnapshotAsync(vin, ct) ?? new VehicleSnapshot { Vin = vin };
                    snapshot.Trips = tripService.MapTripData(vin, response.Odometer, null);
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Odometer stream cancelled for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Odometer stream error for VIN {Vin}", vin);
        }
    }

    private async Task StreamExteriorAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        try
        {
            var client = new ExteriorProtos.ExteriorService.ExteriorServiceClient(channel);
            var request = new ExteriorProtos.GetExteriorRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
            var call = client.GetExterior(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Exterior != null)
                {
                    _logger.LogDebug("Exterior update for VIN {Vin}", vin);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    
                    var snapshot = await cache.GetSnapshotAsync(vin, ct) ?? new VehicleSnapshot { Vin = vin };
                    
                    // Map exterior proto to ExteriorStatus model
                    var ext = response.Exterior;
                    var exteriorMapped = new ExteriorStatus
                    {
                        Timestamp = ext.Timestamp?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ext.Timestamp.Seconds).UtcDateTime : null,
                        CentralLock = FormatLock(ext.CentralLock),
                        TailgateLock = FormatLock(ext.TailgateLock),
                        FrontLeftDoor = FormatOpen(ext.FrontLeftDoor),
                        FrontRightDoor = FormatOpen(ext.FrontRightDoor),
                        RearLeftDoor = FormatOpen(ext.RearLeftDoor),
                        RearRightDoor = FormatOpen(ext.RearRightDoor),
                        Hood = FormatOpen(ext.Hood),
                        Tailgate = FormatOpen(ext.Tailgate),
                        FrontLeftWindow = FormatOpen(ext.FrontLeftWindow),
                        FrontRightWindow = FormatOpen(ext.FrontRightWindow),
                        RearLeftWindow = FormatOpen(ext.RearLeftWindow),
                        RearRightWindow = FormatOpen(ext.RearRightWindow),
                        Sunroof = FormatOpen(ext.Sunroof),
                        TankLid = FormatOpen(ext.TankLid),
                        Alarm = FormatAlarm(ext.Alarm)
                    };
                    
                    if (snapshot.Status == null)
                    {
                        snapshot.Status = new VehicleStatus { Vin = vin, Timestamp = DateTime.UtcNow };
                    }
                    snapshot.Status.Exterior = exteriorMapped;
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Exterior stream cancelled for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exterior stream error for VIN {Vin}", vin);
        }
    }

    private async Task StreamAvailabilityAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        try
        {
            var client = new AvailabilityProtos.AvailabilityService.AvailabilityServiceClient(channel);
            var request = new AvailabilityProtos.GetAvailabilityRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
            var call = client.GetAvailability(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Availability != null)
                {
                    _logger.LogDebug("Availability update for VIN {Vin}: {Status}", vin, response.Availability.AvailabilityStatus);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    
                    var snapshot = await cache.GetSnapshotAsync(vin, ct) ?? new VehicleSnapshot { Vin = vin };
                    
                    // Map availability proto to AvailabilityInfo model
                    var avail = response.Availability;
                    var availabilityMapped = new AvailabilityInfo
                    {
                        Timestamp = avail.Timestamp?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(avail.Timestamp.Seconds).UtcDateTime : null,
                        Status = FormatAvailabilityStatus(avail.AvailabilityStatus),
                        UnavailableReason = FormatUnavailableReason(avail.UnavailableReason),
                        UsageMode = FormatUsageMode(avail.UsageMode)
                    };
                    
                    if (snapshot.Status == null)
                    {
                        snapshot.Status = new VehicleStatus { Vin = vin, Timestamp = DateTime.UtcNow };
                    }
                    snapshot.Status.Availability = availabilityMapped;
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Availability stream cancelled for VIN {Vin}", vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Availability stream error for VIN {Vin}", vin);
        }
    }

    // Formatting helpers for proto enums
    private static string FormatLock(ExteriorProtos.LockStatus s) => s switch
    {
        ExteriorProtos.LockStatus.Locked => "Locked",
        ExteriorProtos.LockStatus.Unlocked => "Unlocked",
        _ => "Unknown"
    };

    private static string FormatOpen(ExteriorProtos.OpenStatus s) => s switch
    {
        ExteriorProtos.OpenStatus.Closed => "Closed",
        ExteriorProtos.OpenStatus.Open => "Open",
        ExteriorProtos.OpenStatus.Ajar => "Ajar",
        _ => "Unknown"
    };

    private static string FormatAlarm(ExteriorProtos.AlarmStatus s) => s switch
    {
        ExteriorProtos.AlarmStatus.Idle => "Idle",
        ExteriorProtos.AlarmStatus.Triggered => "Triggered",
        _ => "Unknown"
    };

    private static string FormatAvailabilityStatus(AvailabilityProtos.AvailabilityStatus s) => s switch
    {
        AvailabilityProtos.AvailabilityStatus.Available => "Available",
        AvailabilityProtos.AvailabilityStatus.Unavailable => "Unavailable",
        _ => "Unknown"
    };

    private static string FormatUnavailableReason(AvailabilityProtos.UnavailableReason r) => r switch
    {
        AvailabilityProtos.UnavailableReason.Unspecified => "None",
        AvailabilityProtos.UnavailableReason.NoInternet => "NoInternet",
        AvailabilityProtos.UnavailableReason.PowerSavingMode => "PowerSavingMode",
        AvailabilityProtos.UnavailableReason.CarInUse => "CarInUse",
        AvailabilityProtos.UnavailableReason.OtaInstallationInProgress => "OtaInProgress",
        AvailabilityProtos.UnavailableReason.StolenVehicleTrackingInProgress => "StolenVehicleTracking",
        AvailabilityProtos.UnavailableReason.ServiceModeActive => "ServiceMode",
        _ => "Unknown"
    };

    private static string FormatUsageMode(AvailabilityProtos.UsageMode m) => m switch
    {
        AvailabilityProtos.UsageMode.Abandoned => "Abandoned",
        AvailabilityProtos.UsageMode.Inactive => "Inactive",
        AvailabilityProtos.UsageMode.Convenience => "Convenience",
        AvailabilityProtos.UsageMode.Active => "Active",
        AvailabilityProtos.UsageMode.Driving => "Driving",
        AvailabilityProtos.UsageMode.EngineOn => "EngineOn",
        AvailabilityProtos.UsageMode.EngineOff => "EngineOff",
        _ => "Unknown"
    };

    /// <summary>
    /// Renew the distributed lock for a VIN to prevent other tasks from taking over.
    /// </summary>
    private async Task<bool> RenewStreamLockAsync(string vin, string lockValue, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var db = redis.GetDatabase();
            var lockKey = $"stream_lock:{vin}";

            // Only renew if we still own the lock (compare lockValue)
            var currentValue = await db.StringGetAsync(lockKey);
            if (currentValue == lockValue)
            {
                await db.KeyExpireAsync(lockKey, LockExpiry);
                _logger.LogDebug("Renewed lock for VIN {Vin}", vin);
                return true;
            }

            _logger.LogWarning("Lock ownership lost for VIN {Vin}", vin);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew lock for VIN {Vin}", vin);
            return false;
        }
    }

    /// <summary>
    /// Release the distributed lock for a VIN when stopping streams.
    /// </summary>
    private async Task ReleaseStreamLockAsync(string vin, string lockValue, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var db = redis.GetDatabase();
            var lockKey = $"stream_lock:{vin}";

            // Only release if we still own the lock (compare lockValue)
            var currentValue = await db.StringGetAsync(lockKey);
            if (currentValue == lockValue)
            {
                await db.KeyDeleteAsync(lockKey);
                _logger.LogInformation("Released lock for VIN {Vin}", vin);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release lock for VIN {Vin}", vin);
        }
    }
}
