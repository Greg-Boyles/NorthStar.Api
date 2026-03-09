using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Models;
using NorthStar.Api.Protos;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using ExteriorProtos = NorthStar.Api.Protos.Exterior;
using AvailabilityProtos = NorthStar.Api.Protos.Availability;
using ClimateProtos = NorthStar.Api.Protos.ParkingClimatization;

namespace NorthStar.Api.Services;

public class VehicleSnapshotService
{
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";

    private readonly PolestarStatusService _statusService;
    private readonly PolestarTripService _tripService;
    private readonly PolestarChargingScheduleService _chargingScheduleService;
    private readonly PolestarClimateScheduleService _climateScheduleService;
    private readonly ILogger<VehicleSnapshotService> _logger;

    public VehicleSnapshotService(
        PolestarStatusService statusService,
        PolestarTripService tripService,
        PolestarChargingScheduleService chargingScheduleService,
        PolestarClimateScheduleService climateScheduleService,
        ILogger<VehicleSnapshotService> logger)
    {
        _statusService = statusService;
        _tripService = tripService;
        _chargingScheduleService = chargingScheduleService;
        _climateScheduleService = climateScheduleService;
        _logger = logger;
    }

    public async Task<VehicleSnapshot> GetSnapshotAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching unified snapshot for VIN: {Vin}", vin);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));

        // Fire all upstream calls in parallel — reuse existing services for PCCS and Health/Status,
        // but do the C3 gRPC calls ourselves with a shared channel + single Battery fetch.
        using var c3Channel = GrpcChannel.ForAddress(C3Host);
        var c3Headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        // C3 gRPC calls — Battery fetched once, shared across battery/trips/status
        var batteryTask = GetBatteryRawAsync(c3Channel, c3Headers, vin, cts.Token);
        var odometerTask = GetOdometerRawAsync(c3Channel, c3Headers, vin, cts.Token);
        var exteriorTask = SafeAwait(PolestarStatusService.GetExteriorAsync(c3Channel, c3Headers, vin, cts.Token), "Exterior");
        var availabilityTask = SafeAwait(PolestarStatusService.GetAvailabilityAsync(c3Channel, c3Headers, vin, cts.Token), "Availability");
        var climateTask = SafeAwait(PolestarStatusService.GetClimateAsync(c3Channel, c3Headers, vin, cts.Token), "Climate");

        // PCCS gRPC calls
        var chargingTask = SafeAwaitNonNull(_chargingScheduleService.GetChargingScheduleAsync(accessToken, vin, cts.Token), "ChargingSchedule");
        var climateScheduleTask = SafeAwaitNonNull(_climateScheduleService.GetClimateScheduleAsync(accessToken, vin, cts.Token), "ClimateSchedule");

        // Health GraphQL
        var healthTask = SafeAwait(_statusService.GetHealthAsync(accessToken, vin, cts.Token), "Health");

        // Await all
        await Task.WhenAll(batteryTask, odometerTask, exteriorTask, availabilityTask, climateTask, chargingTask, climateScheduleTask, healthTask);

        var batteryRaw = await SafeAwait(batteryTask, "Battery");
        var odometerRaw = await SafeAwait(odometerTask, "Odometer");

        // Build snapshot using shared battery data
        return new VehicleSnapshot
        {
            Vin = vin,
            Battery = batteryRaw != null ? _tripService.MapBatteryData(vin, batteryRaw) : null,
            Trips = _tripService.MapTripData(vin, odometerRaw, batteryRaw),
            Status = new VehicleStatus
            {
                Vin = vin,
                Timestamp = DateTime.UtcNow,
                Exterior = await exteriorTask,
                Availability = await availabilityTask,
                Climate = await climateTask,
                Battery = PolestarStatusService.MapBatteryStatus(batteryRaw),
                Health = await healthTask
            },
            ChargingSchedule = await chargingTask,
            ClimateSchedule = await climateScheduleTask
        };
    }

    private static async Task<BatteryProtos.Battery?> GetBatteryRawAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new BatteryProtos.BatteryService.BatteryServiceClient(channel);
        var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestBatteryAsync(request, headers, cancellationToken: ct);
        return response.Battery;
    }

    private static async Task<Odometer?> GetOdometerRawAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new OdometerService.OdometerServiceClient(channel);
        var request = new GetOdometerRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var call = client.GetOdometer(request, headers, cancellationToken: ct);

        if (!await call.ResponseStream.MoveNext(ct))
            return null;

        return call.ResponseStream.Current.Odometer;
    }

    private async Task<T?> SafeAwait<T>(Task<T?> task, string name) where T : class
    {
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Snapshot: failed to fetch {Name}: {Message}", name, ex.Message);
            return null;
        }
    }

    /// <summary>Overload for non-nullable Task returns (e.g. ChargingSchedule, ClimateSchedule).</summary>
    private async Task<T?> SafeAwaitNonNull<T>(Task<T> task, string name) where T : class
    {
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Snapshot: failed to fetch {Name}: {Message}", name, ex.Message);
            return null;
        }
    }
}
