using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using NorthStar.Api.Models;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using OdometerProtos = NorthStar.Api.Protos;

namespace NorthStar.Api.Services;

/// <summary>
/// Redis-backed cache for vehicle state. Stores individual fields (battery, odometer, etc.)
/// with timestamps for ETag support and auto-expiry.
/// </summary>
public class VehicleStateCache
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<VehicleStateCache> _logger;
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(2);

    public VehicleStateCache(IDistributedCache cache, ILogger<VehicleStateCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    // Redis key patterns
    private static string BatteryKey(string vin) => $"vehicle:{vin}:battery";
    private static string OdometerKey(string vin) => $"vehicle:{vin}:odometer";
    private static string ExteriorKey(string vin) => $"vehicle:{vin}:exterior";
    private static string AvailabilityKey(string vin) => $"vehicle:{vin}:availability";
    private static string ClimateKey(string vin) => $"vehicle:{vin}:climate";
    private static string HealthKey(string vin) => $"vehicle:{vin}:health";
    private static string ChargingScheduleKey(string vin) => $"vehicle:{vin}:charging_schedule";
    private static string ClimateScheduleKey(string vin) => $"vehicle:{vin}:climate_schedule";
    private static string LastAccessKey(string vin) => $"tokens:{vin}:lastAccess";
    private static string RefreshTokenKey(string vin) => $"tokens:{vin}:refresh";

    /// <summary>
    /// Get complete vehicle snapshot from cache. Returns null if any required field is missing.
    /// </summary>
    public async Task<VehicleSnapshot?> GetSnapshotAsync(string vin, CancellationToken ct = default)
    {
        _logger.LogDebug("Reading snapshot from cache for VIN: {Vin}", vin);

        var batteryJson = await _cache.GetStringAsync(BatteryKey(vin), ct);
        var odometerJson = await _cache.GetStringAsync(OdometerKey(vin), ct);
        var exteriorJson = await _cache.GetStringAsync(ExteriorKey(vin), ct);
        var availabilityJson = await _cache.GetStringAsync(AvailabilityKey(vin), ct);
        var climateJson = await _cache.GetStringAsync(ClimateKey(vin), ct);
        var healthJson = await _cache.GetStringAsync(HealthKey(vin), ct);
        var chargingScheduleJson = await _cache.GetStringAsync(ChargingScheduleKey(vin), ct);
        var climateScheduleJson = await _cache.GetStringAsync(ClimateScheduleKey(vin), ct);

        // If core fields are missing, return null (cache miss)
        if (batteryJson == null || odometerJson == null)
        {
            _logger.LogDebug("Cache miss for VIN {Vin} (missing core fields)", vin);
            return null;
        }

        // Deserialize cached data
        var battery = JsonSerializer.Deserialize<BatteryData>(batteryJson);
        var trips = JsonSerializer.Deserialize<TripData>(odometerJson);
        var exterior = exteriorJson != null ? JsonSerializer.Deserialize<ExteriorStatus>(exteriorJson) : null;
        var availability = availabilityJson != null ? JsonSerializer.Deserialize<AvailabilityInfo>(availabilityJson) : null;
        var climate = climateJson != null ? JsonSerializer.Deserialize<ClimateStatus>(climateJson) : null;
        var health = healthJson != null ? JsonSerializer.Deserialize<HealthStatus>(healthJson) : null;
        var chargingSchedule = chargingScheduleJson != null ? JsonSerializer.Deserialize<ChargingSchedule>(chargingScheduleJson) : null;
        var climateSchedule = climateScheduleJson != null ? JsonSerializer.Deserialize<ClimateSchedule>(climateScheduleJson) : null;

        return new VehicleSnapshot
        {
            Vin = vin,
            Timestamp = DateTime.UtcNow,
            Battery = battery,
            Trips = trips,
            Status = new VehicleStatus
            {
                Vin = vin,
                Timestamp = DateTime.UtcNow,
                Exterior = exterior,
                Availability = availability,
                Climate = climate,
                Battery = battery != null ? MapBatteryStatus(battery) : null,
                Health = health
            },
            ChargingSchedule = chargingSchedule,
            ClimateSchedule = climateSchedule
        };
    }

    /// <summary>
    /// Set individual cached field with auto-expiry
    /// </summary>
    public async Task SetAsync<T>(string vin, string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value);
        await _cache.SetStringAsync(key, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = DefaultExpiry
        }, ct);
    }

    /// <summary>
    /// Cache complete snapshot (used for fallback/cold start)
    /// </summary>
    public async Task SetSnapshotAsync(string vin, VehicleSnapshot snapshot, CancellationToken ct = default)
    {
        _logger.LogDebug("Writing snapshot to cache for VIN: {Vin}", vin);

        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = DefaultExpiry
        };

        if (snapshot.Battery != null)
            await _cache.SetStringAsync(BatteryKey(vin), JsonSerializer.Serialize(snapshot.Battery), options, ct);

        if (snapshot.Trips != null)
            await _cache.SetStringAsync(OdometerKey(vin), JsonSerializer.Serialize(snapshot.Trips), options, ct);

        if (snapshot.Status?.Exterior != null)
            await _cache.SetStringAsync(ExteriorKey(vin), JsonSerializer.Serialize(snapshot.Status.Exterior), options, ct);

        if (snapshot.Status?.Availability != null)
            await _cache.SetStringAsync(AvailabilityKey(vin), JsonSerializer.Serialize(snapshot.Status.Availability), options, ct);

        if (snapshot.Status?.Climate != null)
            await _cache.SetStringAsync(ClimateKey(vin), JsonSerializer.Serialize(snapshot.Status.Climate), options, ct);

        if (snapshot.Status?.Health != null)
            await _cache.SetStringAsync(HealthKey(vin), JsonSerializer.Serialize(snapshot.Status.Health), options, ct);

        if (snapshot.ChargingSchedule != null)
            await _cache.SetStringAsync(ChargingScheduleKey(vin), JsonSerializer.Serialize(snapshot.ChargingSchedule), options, ct);

        if (snapshot.ClimateSchedule != null)
            await _cache.SetStringAsync(ClimateScheduleKey(vin), JsonSerializer.Serialize(snapshot.ClimateSchedule), options, ct);
    }

    /// <summary>
    /// Get last access timestamp (for auto-cleanup)
    /// </summary>
    public async Task<DateTimeOffset?> GetLastAccessAsync(string vin, CancellationToken ct = default)
    {
        var timestampStr = await _cache.GetStringAsync(LastAccessKey(vin), ct);
        return timestampStr != null && long.TryParse(timestampStr, out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }

    /// <summary>
    /// Update last access timestamp (called on every GET /api/stream/{vin} request)
    /// </summary>
    public async Task UpdateLastAccessAsync(string vin, CancellationToken ct = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        await _cache.SetStringAsync(LastAccessKey(vin), timestamp, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7) // Longer expiry for tracking
        }, ct);
    }

    /// <summary>
    /// Store refresh token for a VIN
    /// </summary>
    public async Task SetRefreshTokenAsync(string vin, string refreshToken, CancellationToken ct = default)
    {
        await _cache.SetStringAsync(RefreshTokenKey(vin), refreshToken, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30) // Refresh tokens live ~30 days
        }, ct);
    }

    /// <summary>
    /// Get refresh token for a VIN
    /// </summary>
    public async Task<string?> GetRefreshTokenAsync(string vin, CancellationToken ct = default)
    {
        return await _cache.GetStringAsync(RefreshTokenKey(vin), ct);
    }

    /// <summary>
    /// Remove all cached data for a VIN (cleanup)
    /// </summary>
    public async Task RemoveAsync(string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Removing all cached data for VIN: {Vin}", vin);

        await Task.WhenAll(
            _cache.RemoveAsync(BatteryKey(vin), ct),
            _cache.RemoveAsync(OdometerKey(vin), ct),
            _cache.RemoveAsync(ExteriorKey(vin), ct),
            _cache.RemoveAsync(AvailabilityKey(vin), ct),
            _cache.RemoveAsync(ClimateKey(vin), ct),
            _cache.RemoveAsync(HealthKey(vin), ct),
            _cache.RemoveAsync(ChargingScheduleKey(vin), ct),
            _cache.RemoveAsync(ClimateScheduleKey(vin), ct),
            _cache.RemoveAsync(LastAccessKey(vin), ct),
            _cache.RemoveAsync(RefreshTokenKey(vin), ct)
        );
    }

    private static BatteryStatus MapBatteryStatus(BatteryData battery)
    {
        return new BatteryStatus
        {
            Timestamp = battery.Timestamp,
            ChargeLevelPercentage = battery.ChargeLevelPercentage,
            EstimatedRangeKm = battery.EstimatedRangeKm,
            EstimatedRangeMiles = battery.EstimatedRangeMiles,
            ChargingStatus = battery.ChargingStatus,
            ChargerConnectionStatus = battery.ChargerConnectionStatus,
            ChargingPowerWatts = battery.ChargingPowerWatts,
            ChargingCurrentAmps = battery.ChargingCurrentAmps,
            ChargingVoltageVolts = battery.ChargingVoltageVolts,
            EstimatedChargingTimeToFullMinutes = battery.EstimatedChargingTimeToFullMinutes
        };
    }
}
