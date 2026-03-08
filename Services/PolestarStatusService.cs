using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using PolestarApi.Models;
using ExteriorProtos = PolestarApi.Protos.Exterior;
using AvailabilityProtos = PolestarApi.Protos.Availability;
using ClimateProtos = PolestarApi.Protos.ParkingClimatization;
using BatteryProtos = PolestarApi.Protos.Battery;

namespace PolestarApi.Services;

public class PolestarStatusService
{
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";
    private const string GraphqlUrl = "https://pc-api.polestar.com/eu-north-1/mystar-v2/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PolestarStatusService> _logger;

    public PolestarStatusService(IHttpClientFactory httpClientFactory, ILogger<PolestarStatusService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<VehicleStatus> GetVehicleStatusAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Querying comprehensive vehicle status for VIN: {Vin}", vin);

        using var channel = GrpcChannel.ForAddress(C3Host);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        // Query all services in parallel
        var exteriorTask = GetExteriorAsync(channel, headers, vin, cts.Token);
        var availabilityTask = GetAvailabilityAsync(channel, headers, vin, cts.Token);
        var climateTask = GetClimateAsync(channel, headers, vin, cts.Token);
        var batteryTask = GetBatteryAsync(channel, headers, vin, cts.Token);
        var healthTask = GetHealthAsync(accessToken, vin, cts.Token);

        // Await all — catching individual failures so partial data is still returned
        var exterior = await SafeAwait(exteriorTask, "Exterior");
        var availability = await SafeAwait(availabilityTask, "Availability");
        var climate = await SafeAwait(climateTask, "Climate");
        var battery = await SafeAwait(batteryTask, "Battery");
        var health = await SafeAwait(healthTask, "Health");

        return new VehicleStatus
        {
            Vin = vin,
            Timestamp = DateTime.UtcNow,
            Exterior = exterior,
            Availability = availability,
            Climate = climate,
            Battery = battery,
            Health = health
        };
    }

    private async Task<T?> SafeAwait<T>(Task<T?> task, string name) where T : class
    {
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch {Name} data: {Message}", name, ex.Message);
            return null;
        }
    }

    // --- Exterior ---

    private static async Task<ExteriorStatus?> GetExteriorAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new ExteriorProtos.ExteriorService.ExteriorServiceClient(channel);
        var request = new ExteriorProtos.GetExteriorRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestExteriorAsync(request, headers, cancellationToken: ct);
        var ext = response.Exterior;
        if (ext == null) return null;

        return new ExteriorStatus
        {
            Timestamp = ToDateTime(ext.Timestamp),
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
    }

    // --- Availability ---

    private static async Task<AvailabilityInfo?> GetAvailabilityAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new AvailabilityProtos.AvailabilityService.AvailabilityServiceClient(channel);
        var request = new AvailabilityProtos.GetAvailabilityRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestAvailabilityAsync(request, headers, cancellationToken: ct);
        var avail = response.Availability;
        if (avail == null) return null;

        return new AvailabilityInfo
        {
            Timestamp = ToDateTime(avail.Timestamp),
            Status = FormatAvailabilityStatus(avail.AvailabilityStatus),
            UnavailableReason = FormatUnavailableReason(avail.UnavailableReason),
            UsageMode = FormatUsageMode(avail.UsageMode)
        };
    }

    // --- Climate ---

    private static async Task<ClimateStatus?> GetClimateAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new ClimateProtos.ParkingClimatizationService.ParkingClimatizationServiceClient(channel);
        var request = new ClimateProtos.GetParkingClimatizationRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestParkingClimatizationAsync(request, headers, cancellationToken: ct);
        var pc = response.ParkingClimatization;
        if (pc == null) return null;

        var result = new ClimateStatus
        {
            Timestamp = ToDateTime(pc.Timestamp),
            RunningStatus = FormatRunningStatus(pc.RunningStatus),
            Ventilation = FormatVentilation(pc.Ventilation),
            StartReason = FormatStartReason(pc.StartReason),
            StartedAt = ToDateTime(pc.StartedAt),
            EndingAt = ToDateTime(pc.EndingAt),
            FrontLeftSeatHeating = FormatHeatingIntensity(pc.HasRequestedFrontLeftSeat ? pc.RequestedFrontLeftSeat : null),
            FrontRightSeatHeating = FormatHeatingIntensity(pc.HasRequestedFrontRightSeat ? pc.RequestedFrontRightSeat : null),
            RearLeftSeatHeating = FormatHeatingIntensity(pc.HasRequestedRearLeftSeat ? pc.RequestedRearLeftSeat : null),
            RearRightSeatHeating = FormatHeatingIntensity(pc.HasRequestedRearRightSeat ? pc.RequestedRearRightSeat : null),
            SteeringWheelHeating = FormatHeatingIntensity(pc.HasRequestedSteeringWheelHeating ? pc.RequestedSteeringWheelHeating : null),
            Errors = pc.Errors.Select(FormatClimateError).ToList(),
            Warnings = pc.Warnings.Select(FormatClimateWarning).ToList()
        };

        if (pc.HasRuntimeLeftMinutes)
            result.RuntimeLeftMinutes = pc.RuntimeLeftMinutes;

        if (pc.HasCurrentCompartmentTemperatureCelsius)
        {
            result.CurrentTemperatureCelsius = (float)Math.Round(pc.CurrentCompartmentTemperatureCelsius, 1);
            result.CurrentTemperatureFahrenheit = (float)Math.Round(pc.CurrentCompartmentTemperatureCelsius * 9f / 5f + 32f, 1);
        }

        if (pc.HasRequestedCompartmentTemperatureCelsius)
        {
            result.RequestedTemperatureCelsius = (float)Math.Round(pc.RequestedCompartmentTemperatureCelsius, 1);
            result.RequestedTemperatureFahrenheit = (float)Math.Round(pc.RequestedCompartmentTemperatureCelsius * 9f / 5f + 32f, 1);
        }

        return result;
    }

    // --- Battery ---

    private static async Task<BatteryStatus?> GetBatteryAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new BatteryProtos.BatteryService.BatteryServiceClient(channel);
        var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestBatteryAsync(request, headers, cancellationToken: ct);
        var b = response.Battery;
        if (b == null) return null;

        return new BatteryStatus
        {
            Timestamp = ToDateTime(b.Timestamp),
            ChargeLevelPercentage = Math.Round(b.BatteryChargeLevelPercentage, 1),
            EstimatedRangeKm = b.EstimatedDistanceToEmptyKm,
            EstimatedRangeMiles = b.EstimatedDistanceToEmptyMiles,
            ChargingStatus = FormatChargingStatus(b.ChargingStatus),
            ChargerConnectionStatus = FormatChargerConnection(b.ChargerConnectionStatus),
            ChargingPowerWatts = b.ChargingPowerWatts,
            ChargingCurrentAmps = b.ChargingCurrentAmps,
            ChargingVoltageVolts = b.ChargingVoltageVolts,
            EstimatedChargingTimeToFullMinutes = b.EstimatedChargingTimeToFullMinutes
        };
    }

    // --- Health (GraphQL) ---

    private async Task<HealthStatus?> GetHealthAsync(string accessToken, string vin, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = JsonSerializer.Serialize(new
        {
            query = HealthQuery,
            variables = new { vins = new[] { vin } }
        });

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(GraphqlUrl, content, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("carTelematicsV2", out var tel)) return null;
        if (!tel.TryGetProperty("health", out var healthArray)) return null;

        foreach (var h in healthArray.EnumerateArray())
        {
            if (h.GetProp("vin") != vin) continue;

            var distKm = h.GetIntProp("distanceToServiceKm");
            return new HealthStatus
            {
                Timestamp = ParseGraphqlTimestamp(h),
                ServiceWarning = h.GetProp("serviceWarning"),
                DaysToService = h.GetIntProp("daysToService"),
                DistanceToServiceKm = distKm,
                DistanceToServiceMiles = distKm.HasValue ? (int)Math.Round(distKm.Value / 1.60934) : null,
                BrakeFluidLevelWarning = h.GetProp("brakeFluidLevelWarning"),
                EngineCoolantLevelWarning = h.GetProp("engineCoolantLevelWarning"),
                OilLevelWarning = h.GetProp("oilLevelWarning"),
                WasherFluidLevelWarning = h.GetProp("washerFluidLevelWarning")
            };
        }

        return null;
    }

    // --- Formatting helpers ---

    private static DateTime? ToDateTime(ExteriorProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    private static DateTime? ToDateTime(AvailabilityProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    private static DateTime? ToDateTime(ClimateProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    private static DateTime? ToDateTime(BatteryProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    private static DateTime? ParseGraphqlTimestamp(JsonElement el)
    {
        if (!el.TryGetProperty("timestamp", out var ts)) return null;
        if (!ts.TryGetProperty("seconds", out var secEl)) return null;
        var seconds = secEl.ValueKind == JsonValueKind.Number
            ? secEl.GetInt64()
            : long.TryParse(secEl.GetString(), out var s) ? s : 0;
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
    }

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

    private static string FormatRunningStatus(ClimateProtos.RunningStatus s) => s switch
    {
        ClimateProtos.RunningStatus.On => "On",
        ClimateProtos.RunningStatus.Off => "Off",
        ClimateProtos.RunningStatus.Pending => "Pending",
        _ => "Unknown"
    };

    private static string FormatVentilation(ClimateProtos.Ventilation v) => v switch
    {
        ClimateProtos.Ventilation.Cooling => "Cooling",
        ClimateProtos.Ventilation.Heating => "Heating",
        ClimateProtos.Ventilation.Neutral => "Neutral",
        _ => "Unknown"
    };

    private static string FormatStartReason(ClimateProtos.StartReason r) => r switch
    {
        ClimateProtos.StartReason.ManuallyFromCar => "ManuallyFromCar",
        ClimateProtos.StartReason.Remote => "Remote",
        ClimateProtos.StartReason.Timer => "Timer",
        ClimateProtos.StartReason.KeepClimate => "KeepClimate",
        _ => "None"
    };

    private static string? FormatHeatingIntensity(ClimateProtos.HeatingIntensity? h) => h switch
    {
        null => null,
        ClimateProtos.HeatingIntensity.Off => "Off",
        ClimateProtos.HeatingIntensity.Low => "Low",
        ClimateProtos.HeatingIntensity.Medium => "Medium",
        ClimateProtos.HeatingIntensity.High => "High",
        _ => "Unknown"
    };

    private static string FormatClimateError(ClimateProtos.ErrorType e) => e switch
    {
        ClimateProtos.ErrorType.FuelLow => "FuelLow",
        ClimateProtos.ErrorType.BatteryLow => "BatteryLow",
        ClimateProtos.ErrorType.NotConnectedToPower => "NotConnectedToPower",
        ClimateProtos.ErrorType.NoStartNeeded => "NoStartNeeded",
        ClimateProtos.ErrorType.ServiceRequired => "ServiceRequired",
        ClimateProtos.ErrorType.TemporarilyNotAvailable => "TemporarilyNotAvailable",
        ClimateProtos.ErrorType.PersistentErrorDetected => "PersistentErrorDetected",
        ClimateProtos.ErrorType.ReachedMaxRuntime => "ReachedMaxRuntime",
        _ => "Unknown"
    };

    private static string FormatClimateWarning(ClimateProtos.WarningType w) => w switch
    {
        ClimateProtos.WarningType.BatteryLow => "BatteryLow",
        ClimateProtos.WarningType.RunTimeNearingLimit => "RunTimeNearingLimit",
        _ => "Unknown"
    };

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

    private const string HealthQuery = @"
        query CarTelematicsV2($vins: [String!]!) {
            carTelematicsV2(vins: $vins) {
                health {
                    vin
                    serviceWarning
                    daysToService
                    distanceToServiceKm
                    brakeFluidLevelWarning
                    engineCoolantLevelWarning
                    oilLevelWarning
                    washerFluidLevelWarning
                    timestamp { seconds nanos }
                }
            }
        }";
}
