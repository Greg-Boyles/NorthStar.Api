namespace PolestarApi.Models;

public class ClimateSchedule
{
    public string? Vin { get; set; }
    public List<ClimateTimerInfo> Timers { get; set; } = new();
    public List<ClimateTimerInfo> PendingTimers { get; set; } = new();
    public List<ClimateTimerInfo> PendingDeleteTimers { get; set; } = new();
    public ClimateTimerSettings? Settings { get; set; }
    public ClimateTimerSettings? PendingSettings { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ClimateTimerInfo
{
    public string? TimerId { get; set; }
    public int Index { get; set; }
    public bool Activated { get; set; }
    public bool Repeat { get; set; }

    // Ready-at time
    public int ReadyAtHour { get; set; }
    public int ReadyAtMinute { get; set; }
    public int TimezoneOffsetMinutes { get; set; }

    // Weekdays (e.g. ["Monday", "Wednesday", "Friday"])
    public List<string> Weekdays { get; set; } = new();

    // Start date (for non-repeating timers)
    public string? StartDate { get; set; }

    public string? SyncStatus { get; set; }
}

public class ClimateTimerSettings
{
    // Temperature
    public bool IsTemperatureRequested { get; set; }
    public float? RequestedTemperatureCelsius { get; set; }
    public float? RequestedTemperatureFahrenheit { get; set; }

    // Seat heating
    public string? FrontLeftSeatHeating { get; set; }
    public string? FrontRightSeatHeating { get; set; }
    public string? RearLeftSeatHeating { get; set; }
    public string? RearRightSeatHeating { get; set; }

    // Steering wheel
    public string? SteeringWheelHeating { get; set; }

    // Battery
    public string? BatteryPreconditioning { get; set; }
}
