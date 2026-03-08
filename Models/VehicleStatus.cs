namespace PolestarApi.Models;

public class VehicleStatus
{
    public string? Vin { get; set; }
    public DateTime? Timestamp { get; set; }

    public ExteriorStatus? Exterior { get; set; }
    public AvailabilityInfo? Availability { get; set; }
    public ClimateStatus? Climate { get; set; }
    public BatteryStatus? Battery { get; set; }
    public HealthStatus? Health { get; set; }
}

// --- Exterior (locks, doors, windows, alarm) ---

public class ExteriorStatus
{
    public DateTime? Timestamp { get; set; }

    // Locks
    public string? CentralLock { get; set; }
    public string? TailgateLock { get; set; }

    // Doors
    public string? FrontLeftDoor { get; set; }
    public string? FrontRightDoor { get; set; }
    public string? RearLeftDoor { get; set; }
    public string? RearRightDoor { get; set; }
    public string? Hood { get; set; }
    public string? Tailgate { get; set; }

    // Windows
    public string? FrontLeftWindow { get; set; }
    public string? FrontRightWindow { get; set; }
    public string? RearLeftWindow { get; set; }
    public string? RearRightWindow { get; set; }
    public string? Sunroof { get; set; }

    // Other
    public string? TankLid { get; set; }
    public string? Alarm { get; set; }
}

// --- Availability (online status, usage mode) ---

public class AvailabilityInfo
{
    public DateTime? Timestamp { get; set; }
    public string? Status { get; set; }
    public string? UnavailableReason { get; set; }
    public string? UsageMode { get; set; }
}

// --- Climate (parking climatization) ---

public class ClimateStatus
{
    public DateTime? Timestamp { get; set; }
    public string? RunningStatus { get; set; }
    public int? RuntimeLeftMinutes { get; set; }
    public string? Ventilation { get; set; }
    public float? CurrentTemperatureCelsius { get; set; }
    public float? CurrentTemperatureFahrenheit { get; set; }
    public float? RequestedTemperatureCelsius { get; set; }
    public float? RequestedTemperatureFahrenheit { get; set; }
    public string? StartReason { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndingAt { get; set; }

    // Seat heating
    public string? FrontLeftSeatHeating { get; set; }
    public string? FrontRightSeatHeating { get; set; }
    public string? RearLeftSeatHeating { get; set; }
    public string? RearRightSeatHeating { get; set; }
    public string? SteeringWheelHeating { get; set; }

    // Issues
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

// --- Battery (charge level, charging status) ---

public class BatteryStatus
{
    public DateTime? Timestamp { get; set; }
    public double ChargeLevelPercentage { get; set; }
    public int EstimatedRangeKm { get; set; }
    public int EstimatedRangeMiles { get; set; }
    public string? ChargingStatus { get; set; }
    public string? ChargerConnectionStatus { get; set; }
    public int ChargingPowerWatts { get; set; }
    public int ChargingCurrentAmps { get; set; }
    public int ChargingVoltageVolts { get; set; }
    public int EstimatedChargingTimeToFullMinutes { get; set; }
}

// --- Health (from GraphQL telematics) ---

public class HealthStatus
{
    public DateTime? Timestamp { get; set; }
    public string? ServiceWarning { get; set; }
    public int? DaysToService { get; set; }
    public int? DistanceToServiceKm { get; set; }
    public int? DistanceToServiceMiles { get; set; }
    public string? BrakeFluidLevelWarning { get; set; }
    public string? EngineCoolantLevelWarning { get; set; }
    public string? OilLevelWarning { get; set; }
    public string? WasherFluidLevelWarning { get; set; }
}
