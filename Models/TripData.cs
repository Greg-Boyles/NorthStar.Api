namespace PolestarApi.Models;

public class TripData
{
    public string? Vin { get; set; }
    public DateTime? Timestamp { get; set; }

    // Total odometer
    public int OdometerMeters { get; set; }
    public double OdometerKm { get; set; }
    public double OdometerMiles { get; set; }

    // Trip Auto (TA) — last trip
    public TripMeter TripAuto { get; set; } = new();

    // Trip Manual (TM) — since reset
    public TripMeter TripManual { get; set; } = new();

    // Trip Since Charge
    public TripMeter TripSinceCharge { get; set; } = new();
}

public class TripMeter
{
    public double DistanceKm { get; set; }
    public double DistanceMiles { get; set; }
    public int AverageSpeedKmh { get; set; }
    public double AverageSpeedMph { get; set; }
    public double AverageConsumptionKwhPer100Km { get; set; }
    public double AverageConsumptionKwhPer100Miles { get; set; }
}

public class BatteryData
{
    public string? Vin { get; set; }
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
    public double AverageConsumptionKwhPer100Km { get; set; }
    public double AverageConsumptionKwhPer100KmAutomatic { get; set; }
    public double AverageConsumptionKwhPer100KmSinceCharge { get; set; }
    public double TotalEnergyConsumptionWh { get; set; }
    public double TotalEnergyConsumptionWhAutomatic { get; set; }
    public double TotalEnergyConsumptionWhSinceCharge { get; set; }
}
