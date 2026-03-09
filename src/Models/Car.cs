namespace NorthStar.Api.Models;

public class Car
{
    public string? Vin { get; set; }
    public string? RegistrationNo { get; set; }
    public string? ModelYear { get; set; }
    public string? ModelName { get; set; }
    public string? Edition { get; set; }
    public string? Market { get; set; }
    public string? FuelType { get; set; }
    public string? Drivetrain { get; set; }
    public int? NumberOfDoors { get; set; }
    public int? NumberOfSeats { get; set; }
    public string? FactoryCompleteDate { get; set; }
    public string? RegistrationDate { get; set; }
    public string? DeliveryDate { get; set; }
    public CarSpecification? Specification { get; set; }
    public CarBattery? Battery { get; set; }
    public CarOdometer? Odometer { get; set; }
    public string? SoftwareVersion { get; set; }
}

public class CarSpecification
{
    public string? Battery { get; set; }
    public string? ElectricMotors { get; set; }
    public string? Performance { get; set; }
    public string? Torque { get; set; }
    public string? TotalHp { get; set; }
    public string? TotalKw { get; set; }
    public string? BodyType { get; set; }
}

public class CarBattery
{
    public int? ChargeLevelPercentage { get; set; }
    public string? ChargingStatus { get; set; }
    public int? EstimatedChargingTimeMinutes { get; set; }
    public int? EstimatedRangeKm { get; set; }
    public DateTime? Timestamp { get; set; }
}

public class CarOdometer
{
    public int? OdometerMeters { get; set; }
    public double? OdometerKm { get; set; }
    public double? OdometerMiles { get; set; }
    public DateTime? Timestamp { get; set; }
}
