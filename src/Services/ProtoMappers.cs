using NorthStar.Api.Models;
using ExteriorProtos = NorthStar.Api.Protos.Exterior;
using AvailabilityProtos = NorthStar.Api.Protos.Availability;
using ClimateProtos = NorthStar.Api.Protos.ParkingClimatization;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using OdometerProtos = NorthStar.Api.Protos;
using ChronosProtos = NorthStar.Api.Protos.Chronos;
using CT = NorthStar.Api.Protos.ClimateTimer;

namespace NorthStar.Api.Services;

/// <summary>
/// Shared mapping helpers for converting gRPC proto types to API models.
/// Used by both streaming (VehicleStreamManager) and snapshot (PolestarStatusService) paths
/// to ensure consistent mapping logic.
/// </summary>
public static class ProtoMappers
{
    // --- Timestamp helpers ---

    public static DateTime? ToDateTime(ExteriorProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    public static DateTime? ToDateTime(AvailabilityProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    public static DateTime? ToDateTime(ClimateProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    public static DateTime? ToDateTime(BatteryProtos.Timestamp? ts) =>
        ts?.Seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).UtcDateTime : null;

    // --- Exterior ---

    public static string FormatLock(ExteriorProtos.LockStatus s) => s switch
    {
        ExteriorProtos.LockStatus.Locked => "Locked",
        ExteriorProtos.LockStatus.Unlocked => "Unlocked",
        _ => "Unknown"
    };

    public static string FormatOpen(ExteriorProtos.OpenStatus s) => s switch
    {
        ExteriorProtos.OpenStatus.Closed => "Closed",
        ExteriorProtos.OpenStatus.Open => "Open",
        ExteriorProtos.OpenStatus.Ajar => "Ajar",
        _ => "Unknown"
    };

    public static string FormatAlarm(ExteriorProtos.AlarmStatus s) => s switch
    {
        ExteriorProtos.AlarmStatus.Idle => "Idle",
        ExteriorProtos.AlarmStatus.Triggered => "Triggered",
        _ => "Unknown"
    };

    public static ExteriorStatus MapExterior(ExteriorProtos.Exterior ext) => new()
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

    // --- Availability ---

    public static string FormatAvailabilityStatus(AvailabilityProtos.AvailabilityStatus s) => s switch
    {
        AvailabilityProtos.AvailabilityStatus.Available => "Available",
        AvailabilityProtos.AvailabilityStatus.Unavailable => "Unavailable",
        _ => "Unknown"
    };

    public static string FormatUnavailableReason(AvailabilityProtos.UnavailableReason r) => r switch
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

    public static string FormatUsageMode(AvailabilityProtos.UsageMode m) => m switch
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

    public static AvailabilityInfo MapAvailability(AvailabilityProtos.Availability avail) => new()
    {
        Timestamp = ToDateTime(avail.Timestamp),
        Status = FormatAvailabilityStatus(avail.AvailabilityStatus),
        UnavailableReason = FormatUnavailableReason(avail.UnavailableReason),
        UsageMode = FormatUsageMode(avail.UsageMode)
    };

    // --- Battery ---

    public static string FormatChargingStatus(int status) => status switch
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

    public static string FormatChargerConnection(int status) => status switch
    {
        1 => "Connected",
        2 => "Disconnected",
        3 => "Fault",
        _ => "Unknown"
    };

    public static BatteryStatus MapBatteryStatus(BatteryProtos.Battery b) => new()
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

    // --- Climate (ParkingClimatization) ---

    public static string FormatRunningStatus(ClimateProtos.RunningStatus s) => s switch
    {
        ClimateProtos.RunningStatus.On => "On",
        ClimateProtos.RunningStatus.Off => "Off",
        ClimateProtos.RunningStatus.Pending => "Pending",
        _ => "Unknown"
    };

    public static string FormatVentilation(ClimateProtos.Ventilation v) => v switch
    {
        ClimateProtos.Ventilation.Cooling => "Cooling",
        ClimateProtos.Ventilation.Heating => "Heating",
        ClimateProtos.Ventilation.Neutral => "Neutral",
        _ => "Unknown"
    };

    public static string FormatStartReason(ClimateProtos.StartReason r) => r switch
    {
        ClimateProtos.StartReason.ManuallyFromCar => "ManuallyFromCar",
        ClimateProtos.StartReason.Remote => "Remote",
        ClimateProtos.StartReason.Timer => "Timer",
        ClimateProtos.StartReason.KeepClimate => "KeepClimate",
        _ => "None"
    };

    public static string? FormatHeatingIntensity(ClimateProtos.HeatingIntensity? h) => h switch
    {
        null => null,
        ClimateProtos.HeatingIntensity.Off => "Off",
        ClimateProtos.HeatingIntensity.Low => "Low",
        ClimateProtos.HeatingIntensity.Medium => "Medium",
        ClimateProtos.HeatingIntensity.High => "High",
        _ => "Unknown"
    };

    public static string FormatClimateError(ClimateProtos.ErrorType e) => e switch
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

    public static string FormatClimateWarning(ClimateProtos.WarningType w) => w switch
    {
        ClimateProtos.WarningType.BatteryLow => "BatteryLow",
        ClimateProtos.WarningType.RunTimeNearingLimit => "RunTimeNearingLimit",
        _ => "Unknown"
    };

    public static ClimateStatus MapClimate(ClimateProtos.ParkingClimatization pc)
    {
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
}
