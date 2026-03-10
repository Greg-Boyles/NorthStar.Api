using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Models;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using OdometerProtos = NorthStar.Api.Protos;
using ExteriorProtos = NorthStar.Api.Protos.Exterior;
using AvailabilityProtos = NorthStar.Api.Protos.Availability;

namespace NorthStar.Api.Services;

/// <summary>
/// Manages gRPC streams for a single VIN.
/// Handles stream lifecycle, token refresh, and lock renewal.
/// </summary>
public class VehicleStreamManager : IDisposable
{
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";
    
    private readonly string _vin;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VehicleStreamManager> _logger;
    private readonly CancellationTokenSource _cancellationSource;
    private readonly List<Task> _streamTasks;
    
    public string Vin => _vin;
    public string LockValue { get; }
    public DateTimeOffset LastTokenRefresh { get; set; }
    public DateTimeOffset LastLockRenewal { get; set; }
    public bool IsRunning => !_cancellationSource.IsCancellationRequested;

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
        _cancellationSource = new CancellationTokenSource();
        _streamTasks = new List<Task>();
        LastTokenRefresh = DateTimeOffset.UtcNow;
        LastLockRenewal = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Start all gRPC streams for this VIN.
    /// </summary>
    public async Task StartStreamsAsync(string accessToken)
    {
        _logger.LogInformation("Starting streams for VIN {Vin}", _vin);

        // Create gRPC channel
        var channel = GrpcChannel.ForAddress(C3Host);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", _vin }
        };

        // Start 4 concurrent gRPC streams (TODO: add remaining 4)
        var ct = _cancellationSource.Token;
        _streamTasks.Add(StreamBatteryAsync(channel, headers, ct));
        _streamTasks.Add(StreamOdometerAsync(channel, headers, ct));
        _streamTasks.Add(StreamExteriorAsync(channel, headers, ct));
        _streamTasks.Add(StreamAvailabilityAsync(channel, headers, ct));

        _logger.LogInformation("All streams started for VIN {Vin}", _vin);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stop all streams and cleanup resources.
    /// </summary>
    public async Task StopStreamsAsync()
    {
        _logger.LogInformation("Stopping streams for VIN {Vin}", _vin);
        
        _cancellationSource.Cancel();
        
        try
        {
            await Task.WhenAll(_streamTasks);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling streams
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for streams to stop for VIN {Vin}", _vin);
        }

        _logger.LogInformation("All streams stopped for VIN {Vin}", _vin);
    }

    // Stream handlers for each gRPC service

    private async Task StreamBatteryAsync(GrpcChannel channel, Metadata headers, CancellationToken ct)
    {
        try
        {
            var client = new BatteryProtos.BatteryService.BatteryServiceClient(channel);
            var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
            var call = client.GetBattery(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Battery != null)
                {
                    _logger.LogInformation("Battery stream update for VIN {Vin}: {Level}%", _vin, response.Battery.BatteryChargeLevelPercentage);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    var tripService = scope.ServiceProvider.GetRequiredService<PolestarTripService>();
                    
                    var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
                    snapshot.Battery = tripService.MapBatteryData(_vin, response.Battery);
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(_vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Battery stream cancelled for VIN {Vin}", _vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Battery stream error for VIN {Vin}", _vin);
        }
    }

    private async Task StreamOdometerAsync(GrpcChannel channel, Metadata headers, CancellationToken ct)
    {
        try
        {
            var client = new OdometerProtos.OdometerService.OdometerServiceClient(channel);
            var request = new OdometerProtos.GetOdometerRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
            var call = client.GetOdometer(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Odometer != null)
                {
                    _logger.LogInformation("Odometer stream update for VIN {Vin}: {Meters}m", _vin, response.Odometer.OdometerMeters);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    var tripService = scope.ServiceProvider.GetRequiredService<PolestarTripService>();
                    
                    var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
                    snapshot.Trips = tripService.MapTripData(_vin, response.Odometer, null);
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(_vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Odometer stream cancelled for VIN {Vin}", _vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Odometer stream error for VIN {Vin}", _vin);
        }
    }

    private async Task StreamExteriorAsync(GrpcChannel channel, Metadata headers, CancellationToken ct)
    {
        try
        {
            var client = new ExteriorProtos.ExteriorService.ExteriorServiceClient(channel);
            var request = new ExteriorProtos.GetExteriorRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
            var call = client.GetExterior(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Exterior != null)
                {
                    _logger.LogInformation("Exterior stream update for VIN {Vin}", _vin);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    
                    var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
                    
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
                        snapshot.Status = new VehicleStatus { Vin = _vin, Timestamp = DateTime.UtcNow };
                    }
                    snapshot.Status.Exterior = exteriorMapped;
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(_vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Exterior stream cancelled for VIN {Vin}", _vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exterior stream error for VIN {Vin}", _vin);
        }
    }

    private async Task StreamAvailabilityAsync(GrpcChannel channel, Metadata headers, CancellationToken ct)
    {
        try
        {
            var client = new AvailabilityProtos.AvailabilityService.AvailabilityServiceClient(channel);
            var request = new AvailabilityProtos.GetAvailabilityRequest { Id = Guid.NewGuid().ToString(), Vin = _vin };
            var call = client.GetAvailability(request, headers, cancellationToken: ct);

            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (response?.Availability != null)
                {
                    _logger.LogInformation("Availability stream update for VIN {Vin}: {Status}", _vin, response.Availability.AvailabilityStatus);
                    
                    using var scope = _serviceProvider.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<VehicleStateCache>();
                    
                    var snapshot = await cache.GetSnapshotAsync(_vin, ct) ?? new VehicleSnapshot { Vin = _vin };
                    
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
                        snapshot.Status = new VehicleStatus { Vin = _vin, Timestamp = DateTime.UtcNow };
                    }
                    snapshot.Status.Availability = availabilityMapped;
                    snapshot.Timestamp = DateTime.UtcNow;
                    
                    await cache.SetSnapshotAsync(_vin, snapshot, ct);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Availability stream cancelled for VIN {Vin}", _vin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Availability stream error for VIN {Vin}", _vin);
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

    public void Dispose()
    {
        _cancellationSource?.Dispose();
    }
}
