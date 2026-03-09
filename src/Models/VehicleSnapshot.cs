namespace NorthStar.Api.Models;

public class VehicleSnapshot
{
    public string? Vin { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public BatteryData? Battery { get; set; }
    public TripData? Trips { get; set; }
    public VehicleStatus? Status { get; set; }
    public ChargingSchedule? ChargingSchedule { get; set; }
    public ClimateSchedule? ClimateSchedule { get; set; }
}
