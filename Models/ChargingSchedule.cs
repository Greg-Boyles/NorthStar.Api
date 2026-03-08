namespace NorthStar.Api.Models;

public class ChargingSchedule
{
    public string? Vin { get; set; }
    public bool Activated { get; set; }
    public ChargeTime? Start { get; set; }
    public ChargeTime? Stop { get; set; }
    public ChargeTimerSchedule? PendingSchedule { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ChargeTime
{
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int TimezoneOffsetMinutes { get; set; }
}

public class ChargeTimerSchedule
{
    public bool Activated { get; set; }
    public ChargeTime? Start { get; set; }
    public ChargeTime? Stop { get; set; }
    public string? SyncStatus { get; set; }
}
