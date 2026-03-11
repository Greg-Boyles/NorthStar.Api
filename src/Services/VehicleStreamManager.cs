using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Models;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using OdometerProtos = NorthStar.Api.Protos;
using ExteriorProtos = NorthStar.Api.Protos.Exterior;
using AvailabilityProtos = NorthStar.Api.Protos.Availability;
using ClimateProtos = NorthStar.Api.Protos.ParkingClimatization;
using ChronosProtos = NorthStar.Api.Protos.Chronos;
using CT = NorthStar.Api.Protos.ClimateTimer;

namespace NorthStar.Api.Services;

/// <summary>
/// Manages gRPC streams for a single VIN.
/// Handles stream lifecycle, retry with exponential backoff, and token-driven stream recycling.
/// 
/// Design notes (from Polestar APK analysis):
/// - The official app uses server-streaming exclusively (never unary GetLatest* calls)
/// - Streams push updates only on vehicle state change — silence is normal for idle cars
/// - The app never keeps streams open longer than the token lifetime (~5 min)
///   because WhileSubscribed() tears them down when UI goes away
/// - We run streams 24/7 so we must proactively recycle them on token refresh
/// </summary>
public class VehicleStreamManager : IDisposable
{
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";
    private const string PccsHost = "https://api.pccs-prod.plstr.io";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly string _vin;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VehicleStreamManager> _logger;

    private CancellationTokenSource _cts;
    private List<Task> _streamTasks;
    private GrpcChannel? _c3Channel;
    private GrpcChannel? _pccsChannel;
    private Metadata _headers;

    public string Vin => _vin;
    public string LockValue { get; }
    public DateTimeOffset LastTokenRefresh { get; set; }
    public DateTimeOffset LastLockRenewal { get; set; }
    public bool IsRunning => !_cts.IsCancellationRequested;

    public VehicleStreamManager(
        string vin,
        string lockValue,
        IServiceProvider serviceProvider,
        ILogger<VehicleStreamManager> logger)
    {
        _vin = vin;
        LockValue = lockValue;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _streamTasks = new List<Task>();
        _headers = new Metadata();
        LastTokenRefresh = DateTimeOffset.UtcNow;
        LastLockRenewal = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Start all gRPC streams for this VIN.
    /// </summary>
    public Task StartStreamsAsync(string accessToken)
    {
        _logger.LogInformation("Starting streams for VIN {Vin}", _vin);

        _headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", _vin }
        };

        _c3Channel = GrpcChannel.ForAddress(C3Host);
        _pccsChannel = GrpcChannel.ForAddress(PccsHost);

        var ct = _cts.Token;

        // C3 vehicle state streams
        _streamTasks.Add(RunStreamWithRetryAsync("Battery", ct, StreamBatteryAsync));
        _streamTasks.Add(RunStreamWithRetryAsync("Odometer", ct, StreamOdometerAsync));
        _streamTasks.Add(RunStreamWithRetryAsync("Exterior", ct, StreamExteriorAsync));
        _streamTasks.Add(RunStreamWithRetryAsync("Availability", ct, StreamAvailabilityAsync));
        _streamTasks.Add(RunStreamWithRetryAsync("Climate", ct, StreamParkingClimatizationAsync));

        // PCCS Chronos streams (different host)
        _streamTasks.Add(RunStreamWithRetryAsync("GlobalChargeTimer", ct, StreamGlobalChargeTimerAsync));
        _streamTasks.Add(RunStreamWithRetryAsync("ParkingClimateTimer", ct, StreamParkingClimateTimerAsync));

        _logger.LogInformation("All {Count} streams started for VIN {Vin}", _streamTasks.Count, _vin);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recycle all streams with a fresh access token.
    /// Cancels existing streams, recreates channels/headers, and restarts.
    /// Called proactively by VehicleStreamService every ~4 min before token expiry.
    /// </summary>
    public async Task UpdateTokenAsync(string newAccessToken)
    {
        _logger.LogInformation("Recycling streams with fresh token for VIN {Vin}", _vin);

        // Cancel current streams
        _cts.Cancel();

        try
        {
            await Task.WhenAll(_streamTasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for streams to stop during token update for VIN {Vin}", _vin);
        }

        // Dispose old resources
        _cts.Dispose();
        _c3Channel?.Dispose();
        _pccsChannel?.Dispose();

        // Create fresh resources
        _cts = new CancellationTokenSource();
        _streamTasks = new List<Task>();

        // Restart with new token
        await StartStreamsAsync(newAccessToken);
        LastTokenRefresh = DateTimeOffset.UtcNow;

        _logger.LogInformation("Streams recycled successfully for VIN {Vin}", _vin);
    }

    /// <summary>
    /// Stop all streams and cleanup resources.
    /// </summary>
    public async Task StopStreamsAsync()
    {
        _logger.LogInformation("Stopping streams for VIN {Vin}", _vin);

        _cts.Cancel();

        try
        {
            await Task.WhenAll(_streamTasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for streams to stop for VIN {Vin}", _vin);
        }

        _logger.LogInformation("All streams stopped for VIN {Vin}", _vin);
    }

    // --- Retry wrapper ---

    /// <summary>
    /// Wraps a stream method with exponential backoff retry.
    /// Matches the Polestar app's Flow.retryWhen() pattern.
    /// </summary>
    private async Task RunStreamWithRetryAsync(string streamName, CancellationToken ct, Func<CancellationToken, Task> streamFunc)
    {
        var delay = InitialRetryDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await streamFunc(ct);
                // Stream completed normally (server closed it) — retry
                _logger.LogInformation("{Stream} stream completed for VIN {Vin}, reconnecting", streamName, _vin);
                delay = InitialRetryDelay; // Reset backoff on successful connection
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                _logger.LogInformation("{Stream} stream cancelled for VIN {Vin}", streamName, _vin);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated || ex.StatusCode == StatusCode.PermissionDenied)
            {
                _logger.LogWarning("{Stream} stream auth error for VIN {Vin}: {Status}. Waiting for token refresh.",
                    streamName, _vin, ex.StatusCode);
                
                // Wait for token recycle (UpdateTokenAsync will cancel us)
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Stream} stream error for VIN {Vin}", streamName, _vin);
            }

            if (ct.IsCancellationRequested) return;

            _logger.LogInformation("{Stream} stream reconnecting in {Delay}s for VIN {Vin}",
                streamName, delay.TotalSeconds, _vin);

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
        }
    }

    // --- C3 stream handlers ---

    private async Task StreamBatteryAsync(CancellationToken ct)
    {
        var client = new BatteryProtos.BatteryService.BatteryServiceClient(_c3Channel);
        var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
        var call = client.GetBattery(request, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response?.Battery == null) continue;

            _logger.LogInformation("Battery stream update for VIN {Vin}: {Level}%",
                _vin, response.Battery.BatteryChargeLevelPercentage);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
            var tripService = scope.ServiceProvider.GetRequiredService<PolestarTripService>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.Battery = tripService.MapBatteryData(_vin, response.Battery);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    private async Task StreamOdometerAsync(CancellationToken ct)
    {
        var client = new OdometerProtos.OdometerService.OdometerServiceClient(_c3Channel);
        var request = new OdometerProtos.GetOdometerRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
        var call = client.GetOdometer(request, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response?.Odometer == null) continue;

            _logger.LogInformation("Odometer stream update for VIN {Vin}: {Meters}m",
                _vin, response.Odometer.OdometerMeters);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
            var tripService = scope.ServiceProvider.GetRequiredService<PolestarTripService>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.Trips = tripService.MapTripData(_vin, response.Odometer, null);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    private async Task StreamExteriorAsync(CancellationToken ct)
    {
        var client = new ExteriorProtos.ExteriorService.ExteriorServiceClient(_c3Channel);
        var request = new ExteriorProtos.GetExteriorRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
        var call = client.GetExterior(request, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response?.Exterior == null) continue;

            _logger.LogInformation("Exterior stream update for VIN {Vin}", _vin);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.Status ??= new VehicleStatus { Vin = _vin, Timestamp = DateTime.UtcNow };
            snapshot.Status.Exterior = ProtoMappers.MapExterior(response.Exterior);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    private async Task StreamAvailabilityAsync(CancellationToken ct)
    {
        var client = new AvailabilityProtos.AvailabilityService.AvailabilityServiceClient(_c3Channel);
        var request = new AvailabilityProtos.GetAvailabilityRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
        var call = client.GetAvailability(request, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response?.Availability == null) continue;

            _logger.LogInformation("Availability stream update for VIN {Vin}: {Status}",
                _vin, response.Availability.AvailabilityStatus);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.Status ??= new VehicleStatus { Vin = _vin, Timestamp = DateTime.UtcNow };
            snapshot.Status.Availability = ProtoMappers.MapAvailability(response.Availability);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    private async Task StreamParkingClimatizationAsync(CancellationToken ct)
    {
        var client = new ClimateProtos.ParkingClimatizationService.ParkingClimatizationServiceClient(_c3Channel);
        var request = new ClimateProtos.GetParkingClimatizationRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
        var call = client.GetParkingClimatization(request, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response?.ParkingClimatization == null) continue;

            _logger.LogInformation("Climate stream update for VIN {Vin}: {Status}",
                _vin, response.ParkingClimatization.RunningStatus);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.Status ??= new VehicleStatus { Vin = _vin, Timestamp = DateTime.UtcNow };
            snapshot.Status.Climate = ProtoMappers.MapClimate(response.ParkingClimatization);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    // --- PCCS Chronos stream handlers ---

    private async Task StreamGlobalChargeTimerAsync(CancellationToken ct)
    {
        var client = new ChronosProtos.GlobalChargeTimerService.GlobalChargeTimerServiceClient(_pccsChannel);
        var request = new ChronosProtos.GetGlobalChargeTimerRequest
        {
            Request = new ChronosProtos.ChronosRequest
            {
                Id = Guid.NewGuid().ToString(),
                Vin = _vin,
                Source = "mobile",
                TimeZone = new ChronosProtos.TimeZone
                {
                    OffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes
                }
            }
        };
        var call = client.GetGlobalChargeTimerStream(request, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            _logger.LogInformation("GlobalChargeTimer stream update for VIN {Vin}", _vin);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.ChargingSchedule = MapChargingSchedule(response);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    private async Task StreamParkingClimateTimerAsync(CancellationToken ct)
    {
        var client = new CT.ParkingClimateTimerService.ParkingClimateTimerServiceClient(_pccsChannel);
        var chronosReq = new CT.ChronosRequest
        {
            Id = Guid.NewGuid().ToString(),
            Vin = _vin,
            Source = "mobile",
            TimeZone = new CT.TimeZone
            {
                OffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes
            }
        };

        var timersRequest = new CT.GetParkingClimateTimersRequest { Request = chronosReq };
        var call = client.GetTimers(timersRequest, _headers, cancellationToken: ct);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            _logger.LogInformation("ParkingClimateTimer stream update for VIN {Vin}", _vin);

            using var scope = _serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();

            var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
            snapshot.ClimateSchedule = MapClimateTimers(response);
            snapshot.Timestamp = DateTime.UtcNow;

            await cache.SetSnapshotAsync(_vin, snapshot, ct);
        }
    }

    // --- PCCS mapping helpers ---

    private ChargingSchedule MapChargingSchedule(ChronosProtos.GetGlobalChargeTimerResponse response)
    {
        var result = new ChargingSchedule
        {
            Vin = _vin,
            Activated = response.GlobalChargeTimer?.Activated ?? false,
            Start = MapChargeTime(response.GlobalChargeTimer?.Start),
            Stop = MapChargeTime(response.GlobalChargeTimer?.Stop),
            UpdatedAt = response.UpdatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(response.UpdatedAt).UtcDateTime
                : null
        };

        if (response.PendingGlobalChargeTimer != null)
        {
            result.PendingSchedule = new ChargeTimerSchedule
            {
                Activated = response.PendingGlobalChargeTimer.Activated,
                Start = MapChargeTime(response.PendingGlobalChargeTimer.Start),
                Stop = MapChargeTime(response.PendingGlobalChargeTimer.Stop),
                SyncStatus = FormatChronosSyncStatus(response.PendingGlobalChargeTimer.Metadata?.SyncStatus)
            };
        }

        return result;
    }

    private static ChargeTime? MapChargeTime(ChronosProtos.DailyTime? dt)
    {
        if (dt == null) return null;
        return new ChargeTime
        {
            Hour = dt.Hour,
            Minute = dt.Minute,
            TimezoneOffsetMinutes = dt.TimeZone?.OffsetMinutes ?? 0
        };
    }

    private static string? FormatChronosSyncStatus(ChronosProtos.SyncStatus? ss)
    {
        if (ss == null) return null;
        var statusStr = ss.Status switch
        {
            ChronosProtos.Status.Sent => "Sent",
            ChronosProtos.Status.Delivered => "Delivered",
            ChronosProtos.Status.Success => "Success",
            ChronosProtos.Status.Synced => "Synced",
            ChronosProtos.Status.CarOffline => "CarOffline",
            ChronosProtos.Status.CarError => "CarError",
            ChronosProtos.Status.Error => "Error",
            ChronosProtos.Status.Replaced => "Replaced",
            _ => "Unknown"
        };
        return string.IsNullOrEmpty(ss.Message) ? statusStr : $"{statusStr}: {ss.Message}";
    }

    private ClimateSchedule MapClimateTimers(CT.GetParkingClimateTimersResponse response)
    {
        return new ClimateSchedule
        {
            Vin = _vin,
            Timers = response.ParkingClimateTimers.Select(MapClimateTimer).ToList(),
            PendingTimers = response.PendingParkingClimateTimers.Select(MapClimateTimer).ToList(),
            PendingDeleteTimers = response.PendingDeleteParkingClimateTimers.Select(MapClimateTimer).ToList(),
            UpdatedAt = response.UpdatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(response.UpdatedAt).UtcDateTime
                : null
        };
    }

    private static ClimateTimerInfo MapClimateTimer(CT.ParkingClimateTimer t)
    {
        var info = new ClimateTimerInfo
        {
            TimerId = t.TimerId,
            Index = t.Index,
            Activated = t.Activated,
            Repeat = t.Repeat,
            ReadyAtHour = t.ReadyAt?.Hour ?? 0,
            ReadyAtMinute = t.ReadyAt?.Minute ?? 0,
            TimezoneOffsetMinutes = t.ReadyAt?.TimeZone?.OffsetMinutes ?? 0,
            Weekdays = t.Weekdays.Select(FormatWeekday).Where(w => w != null).Select(w => w!).ToList(),
            SyncStatus = FormatClimateTimerSyncStatus(t.Metadata?.SyncStatus)
        };

        if (t.StartDate != null && t.StartDate.Year > 0)
            info.StartDate = $"{t.StartDate.Year:D4}-{t.StartDate.Month:D2}-{t.StartDate.Day:D2}";

        return info;
    }

    private static string? FormatWeekday(CT.Weekday w) => w switch
    {
        CT.Weekday.Monday => "Monday",
        CT.Weekday.Tuesday => "Tuesday",
        CT.Weekday.Wednesday => "Wednesday",
        CT.Weekday.Thursday => "Thursday",
        CT.Weekday.Friday => "Friday",
        CT.Weekday.Saturday => "Saturday",
        CT.Weekday.Sunday => "Sunday",
        _ => null
    };

    private static string? FormatClimateTimerSyncStatus(CT.SyncStatus? ss)
    {
        if (ss == null) return null;
        var statusStr = ss.Status switch
        {
            CT.Status.Sent => "Sent",
            CT.Status.Delivered => "Delivered",
            CT.Status.Success => "Success",
            CT.Status.Synced => "Synced",
            CT.Status.CarOffline => "CarOffline",
            CT.Status.CarError => "CarError",
            CT.Status.Error => "Error",
            CT.Status.Replaced => "Replaced",
            _ => "Unknown"
        };
        return string.IsNullOrEmpty(ss.Message) ? statusStr : $"{statusStr}: {ss.Message}";
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _c3Channel?.Dispose();
        _pccsChannel?.Dispose();
    }
}
