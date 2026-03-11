using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Interfaces;
using NorthStar.Api.Models;
using CT = NorthStar.Api.Protos.ClimateTimer;

namespace NorthStar.Api.Services;

public class PolestarClimateScheduleService : IClimateScheduleService
{
    private const string PccsHost = "https://api.pccs-prod.plstr.io";
    private readonly ILogger<PolestarClimateScheduleService> _logger;

    public PolestarClimateScheduleService(ILogger<PolestarClimateScheduleService> logger)
    {
        _logger = logger;
    }

    public async Task<ClimateSchedule> GetClimateScheduleAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Querying climate schedule for VIN: {Vin}", vin);

        using var channel = GrpcChannel.ForAddress(PccsHost);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var client = new CT.ParkingClimateTimerService.ParkingClimateTimerServiceClient(channel);

        var chronosReq = new CT.ChronosRequest
        {
            Id = Guid.NewGuid().ToString(),
            Vin = vin,
            Source = "mobile",
            TimeZone = new CT.TimeZone { OffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes }
        };

        // Query timers and settings in parallel
        var timersTask = GetTimersAsync(client, chronosReq, headers, cts.Token);
        var settingsTask = GetSettingsAsync(client, chronosReq, headers, cts.Token);

        // Timers is the primary call — let errors propagate
        var timersResp = await timersTask;

        // Settings is secondary — tolerate failure
        CT.GetParkingClimateTimerSettingsResponse? settingsResp = null;
        try { settingsResp = await settingsTask; }
        catch (Exception ex) { _logger.LogWarning("Failed to fetch climate timer settings: {Message}", ex.Message); }

        var result = new ClimateSchedule
        {
            Vin = vin,
            Timers = timersResp?.ParkingClimateTimers.Select(MapTimer).ToList() ?? new(),
            PendingTimers = timersResp?.PendingParkingClimateTimers.Select(MapTimer).ToList() ?? new(),
            PendingDeleteTimers = timersResp?.PendingDeleteParkingClimateTimers.Select(MapTimer).ToList() ?? new(),
            UpdatedAt = timersResp?.UpdatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(timersResp.UpdatedAt).UtcDateTime
                : null
        };

        if (settingsResp != null)
        {
            result.Settings = MapSettings(settingsResp.TimerSettings);
            result.PendingSettings = settingsResp.PendingTimerSettings != null
                ? MapSettings(settingsResp.PendingTimerSettings)
                : null;
        }

        return result;
    }

    private static async Task<CT.GetParkingClimateTimersResponse?> GetTimersAsync(
        CT.ParkingClimateTimerService.ParkingClimateTimerServiceClient client,
        CT.ChronosRequest chronosReq, Metadata headers, CancellationToken ct)
    {
        var req = new CT.GetParkingClimateTimersRequest { Request = chronosReq };
        var call = client.GetTimers(req, headers, cancellationToken: ct);
        if (!await call.ResponseStream.MoveNext(ct))
            return null;
        return call.ResponseStream.Current;
    }

    private static async Task<CT.GetParkingClimateTimerSettingsResponse?> GetSettingsAsync(
        CT.ParkingClimateTimerService.ParkingClimateTimerServiceClient client,
        CT.ChronosRequest chronosReq, Metadata headers, CancellationToken ct)
    {
        var req = new CT.GetParkingClimateTimerSettingsRequest { Request = chronosReq };
        var call = client.GetTimerSettings(req, headers, cancellationToken: ct);
        if (!await call.ResponseStream.MoveNext(ct))
            return null;
        return call.ResponseStream.Current;
    }

    // --- Mappers ---

    private static ClimateTimerInfo MapTimer(CT.ParkingClimateTimer t)
    {
        var info = new ClimateTimerInfo
        {
            TimerId = t.TimerId,
            Index = t.Index,
            Activated = t.Activated,
            Repeat = t.Repeat,
            ReadyAtHour = t.ReadyAt?.Hour ?? 0,
            ReadyAtMinute = t.ReadyAt?.Minute ?? 0,
            TimezoneOffsetMinutes = t.ReadyAt?.TimeZone?.OffsetMinutes ?? 0,
            Weekdays = t.Weekdays.Select(FormatWeekday).Where(w => w != null).Select(w => w!).ToList(),
            SyncStatus = FormatSyncStatus(t.Metadata?.SyncStatus)
        };

        if (t.StartDate != null && t.StartDate.Year > 0)
            info.StartDate = $"{t.StartDate.Year:D4}-{t.StartDate.Month:D2}-{t.StartDate.Day:D2}";

        return info;
    }

    private static ClimateTimerSettings? MapSettings(CT.TimerSettings? s)
    {
        if (s == null) return null;

        var settings = new ClimateTimerSettings
        {
            IsTemperatureRequested = s.IsCompartmentTemperatureRequested,
            SteeringWheelHeating = FormatIntensity(s.SteeringWheelHeatingIntensity),
            BatteryPreconditioning = FormatBatteryPreconditioning(s.BatteryPreconditioning)
        };

        if (s.IsCompartmentTemperatureRequested)
        {
            settings.RequestedTemperatureCelsius = (float)Math.Round(s.RequestedCompartmentTemperatureCelsius, 1);
            settings.RequestedTemperatureFahrenheit = (float)Math.Round(s.RequestedCompartmentTemperatureCelsius * 9f / 5f + 32f, 1);
        }

        if (s.SeatHeatingIntensity != null)
        {
            settings.FrontLeftSeatHeating = FormatIntensity(s.SeatHeatingIntensity.FrontRowLeftSeat);
            settings.FrontRightSeatHeating = FormatIntensity(s.SeatHeatingIntensity.FrontRowRightSeat);
            settings.RearLeftSeatHeating = FormatIntensity(s.SeatHeatingIntensity.RearRowLeftSeat);
            settings.RearRightSeatHeating = FormatIntensity(s.SeatHeatingIntensity.RearRowRightSeat);
        }

        return settings;
    }

    private static string? FormatWeekday(CT.Weekday w) => w switch
    {
        CT.Weekday.Monday => "Monday",
        CT.Weekday.Tuesday => "Tuesday",
        CT.Weekday.Wednesday => "Wednesday",
        CT.Weekday.Thursday => "Thursday",
        CT.Weekday.Friday => "Friday",
        CT.Weekday.Saturday => "Saturday",
        CT.Weekday.Sunday => "Sunday",
        _ => null
    };

    private static string FormatIntensity(CT.Intensity i) => i switch
    {
        CT.Intensity.IOff => "Off",
        CT.Intensity.ILevel1 => "Level1",
        CT.Intensity.ILevel2 => "Level2",
        CT.Intensity.ILevel3 => "Level3",
        _ => "Undefined"
    };

    private static string FormatBatteryPreconditioning(CT.BatteryPreconditioning bp) => bp switch
    {
        CT.BatteryPreconditioning.BpOff => "Off",
        CT.BatteryPreconditioning.BpWhenPlugged => "WhenPlugged",
        CT.BatteryPreconditioning.BpOn => "On",
        _ => "Undefined"
    };

    private static string? FormatSyncStatus(CT.SyncStatus? ss)
    {
        if (ss == null) return null;
        var statusStr = ss.Status switch
        {
            CT.Status.Sent => "Sent",
            CT.Status.Delivered => "Delivered",
            CT.Status.Success => "Success",
            CT.Status.Synced => "Synced",
            CT.Status.CarOffline => "CarOffline",
            CT.Status.CarError => "CarError",
            CT.Status.Error => "Error",
            CT.Status.Replaced => "Replaced",
            _ => "Unknown"
        };
        return string.IsNullOrEmpty(ss.Message) ? statusStr : $"{statusStr}: {ss.Message}";
    }
}
