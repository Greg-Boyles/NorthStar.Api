using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Models;
using NorthStar.Models;
using NorthStar.Protos;
using BatteryProtos = NorthStar.Protos.Battery;

namespace NorthStar.Services;

public class PolestarTripService
{
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";
    private readonly ILogger<PolestarTripService> _logger;

    public PolestarTripService(ILogger<PolestarTripService> logger)
    {
        _logger = logger;
    }

    public async Task<TripData> GetTripDataAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Querying odometer/trip + consumption data for VIN: {Vin}", vin);

        using var channel = GrpcChannel.ForAddress(C3Host);

        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        // Query odometer and battery in parallel
        var odometerTask = GetOdometerDataAsync(channel, headers, vin, cts.Token);
        var batteryTask = GetBatteryDataAsync(channel, headers, vin, cts.Token);

        var odo = await odometerTask;
        BatteryProtos.Battery? battery = null;
        try
        {
            battery = await batteryTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch battery/consumption data: {Message}", ex.Message);
        }

        var timestamp = odo.Timestamp != null
            ? DateTimeOffset.FromUnixTimeSeconds(odo.Timestamp.Seconds).UtcDateTime
            : (DateTime?)null;

        return new TripData
        {
            Vin = vin,
            Timestamp = timestamp,
            OdometerMeters = odo.OdometerMeters,
            OdometerKm = Math.Round(odo.OdometerMeters / 1000.0, 1),
            OdometerMiles = Math.Round(odo.OdometerMeters / 1609.344, 1),
            TripAuto = new TripMeter
            {
                DistanceKm = Math.Round(odo.TripMeterAutomaticKm, 1),
                DistanceMiles = Math.Round(odo.TripMeterAutomaticKm * 0.621371, 1),
                AverageSpeedKmh = odo.AverageSpeedKmPerHourAutomatic,
                AverageSpeedMph = Math.Round(odo.AverageSpeedKmPerHourAutomatic * 0.621371, 1),
                AverageConsumptionKwhPer100Km = Math.Round(battery?.AverageEnergyConsumptionKwhPer100KmAutomatic ?? 0, 1),
                AverageConsumptionKwhPer100Miles = Math.Round((battery?.AverageEnergyConsumptionKwhPer100KmAutomatic ?? 0) * 1.60934, 1)
            },
            TripManual = new TripMeter
            {
                DistanceKm = Math.Round(odo.TripMeterManualKm, 1),
                DistanceMiles = Math.Round(odo.TripMeterManualKm * 0.621371, 1),
                AverageSpeedKmh = odo.AverageSpeedKmPerHour,
                AverageSpeedMph = Math.Round(odo.AverageSpeedKmPerHour * 0.621371, 1),
                AverageConsumptionKwhPer100Km = Math.Round(battery?.AverageEnergyConsumptionKwhPer100Km ?? 0, 1),
                AverageConsumptionKwhPer100Miles = Math.Round((battery?.AverageEnergyConsumptionKwhPer100Km ?? 0) * 1.60934, 1)
            },
            TripSinceCharge = new TripMeter
            {
                DistanceKm = Math.Round(odo.TripMeterSinceChargeKm, 1),
                DistanceMiles = Math.Round(odo.TripMeterSinceChargeKm * 0.621371, 1),
                AverageSpeedKmh = odo.AverageSpeedKmPerHourSinceCharge,
                AverageSpeedMph = Math.Round(odo.AverageSpeedKmPerHourSinceCharge * 0.621371, 1),
                AverageConsumptionKwhPer100Km = Math.Round(battery?.AverageEnergyConsumptionKwhPer100KmSinceCharge ?? 0, 1),
                AverageConsumptionKwhPer100Miles = Math.Round((battery?.AverageEnergyConsumptionKwhPer100KmSinceCharge ?? 0) * 1.60934, 1)
            }
        };
    }

    private static async Task<Odometer> GetOdometerDataAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new OdometerService.OdometerServiceClient(channel);
        var request = new GetOdometerRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var call = client.GetOdometer(request, headers, cancellationToken: ct);

        if (!await call.ResponseStream.MoveNext(ct))
            throw new InvalidOperationException("No odometer data received from stream");

        return call.ResponseStream.Current.Odometer
            ?? throw new InvalidOperationException("Response contained no odometer data");
    }

    private static async Task<BatteryProtos.Battery?> GetBatteryDataAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new BatteryProtos.BatteryService.BatteryServiceClient(channel);
        var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestBatteryAsync(request, headers, cancellationToken: ct);
        return response.Battery;
    }

    public async Task<BatteryData> GetBatteryAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Querying battery data for VIN: {Vin}", vin);

        using var channel = GrpcChannel.ForAddress(C3Host);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var battery = await GetBatteryDataAsync(channel, headers, vin, cts.Token)
            ?? throw new InvalidOperationException("No battery data received");

        var timestamp = battery.Timestamp != null
            ? DateTimeOffset.FromUnixTimeSeconds(battery.Timestamp.Seconds).UtcDateTime
            : (DateTime?)null;

        return new BatteryData
        {
            Vin = vin,
            Timestamp = timestamp,
            ChargeLevelPercentage = Math.Round(battery.BatteryChargeLevelPercentage, 1),
            EstimatedRangeKm = battery.EstimatedDistanceToEmptyKm,
            EstimatedRangeMiles = battery.EstimatedDistanceToEmptyMiles,
            ChargingStatus = FormatChargingStatus(battery.ChargingStatus),
            ChargerConnectionStatus = FormatChargerConnection(battery.ChargerConnectionStatus),
            ChargingPowerWatts = battery.ChargingPowerWatts,
            ChargingCurrentAmps = battery.ChargingCurrentAmps,
            ChargingVoltageVolts = battery.ChargingVoltageVolts,
            EstimatedChargingTimeToFullMinutes = battery.EstimatedChargingTimeToFullMinutes,
            AverageConsumptionKwhPer100Km = Math.Round(battery.AverageEnergyConsumptionKwhPer100Km, 1),
            AverageConsumptionKwhPer100KmAutomatic = Math.Round(battery.AverageEnergyConsumptionKwhPer100KmAutomatic, 1),
            AverageConsumptionKwhPer100KmSinceCharge = Math.Round(battery.AverageEnergyConsumptionKwhPer100KmSinceCharge, 1),
            TotalEnergyConsumptionWh = Math.Round(battery.TotalEnergyConsumptionWh, 1),
            TotalEnergyConsumptionWhAutomatic = Math.Round(battery.TotalEnergyConsumptionWhAutomatic, 1),
            TotalEnergyConsumptionWhSinceCharge = Math.Round(battery.TotalEnergyConsumptionWhSinceCharge, 1)
        };
    }

    private static string FormatChargingStatus(int status) => status switch
    {
        1 => "Charging",
        2 => "Idle",
        3 => "Scheduled",
        4 => "Discharging",
        5 => "Error",
        6 => "SmartCharging",
        7 => "Done",
        8 => "SmartChargingPaused",
        _ => "Unknown"
    };

    private static string FormatChargerConnection(int status) => status switch
    {
        1 => "Connected",
        2 => "Disconnected",
        3 => "Fault",
        _ => "Unknown"
    };
}
