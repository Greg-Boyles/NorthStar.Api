using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Models;
using ChronosProtos = NorthStar.Api.Protos.Chronos;

namespace NorthStar.Api.Services;

public class PolestarChargingScheduleService
{
    private const string PccsHost = "https://api.pccs-prod.plstr.io";
    private readonly ILogger<PolestarChargingScheduleService> _logger;

    public PolestarChargingScheduleService(ILogger<PolestarChargingScheduleService> logger)
    {
        _logger = logger;
    }

    public async Task<ChargingSchedule> GetChargingScheduleAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Querying charging schedule for VIN: {Vin}", vin);

        using var channel = GrpcChannel.ForAddress(PccsHost);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var client = new ChronosProtos.GlobalChargeTimerService.GlobalChargeTimerServiceClient(channel);

        var tzOffset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
        var request = new ChronosProtos.GetGlobalChargeTimerRequest
        {
            Request = new ChronosProtos.ChronosRequest
            {
                Id = Guid.NewGuid().ToString(),
                Vin = vin,
                Source = "mobile",
                TimeZone = new ChronosProtos.TimeZone { OffsetMinutes = tzOffset }
            }
        };

        var call = client.GetGlobalChargeTimerStream(request, headers, cancellationToken: cts.Token);

        if (!await call.ResponseStream.MoveNext(cts.Token))
            throw new InvalidOperationException("No charging schedule data received from stream");

        var response = call.ResponseStream.Current;

        var result = new ChargingSchedule
        {
            Vin = vin,
            Activated = response.GlobalChargeTimer?.Activated ?? false,
            Start = MapTime(response.GlobalChargeTimer?.Start),
            Stop = MapTime(response.GlobalChargeTimer?.Stop),
            UpdatedAt = response.UpdatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(response.UpdatedAt).UtcDateTime
                : null
        };

        if (response.PendingGlobalChargeTimer != null)
        {
            result.PendingSchedule = new ChargeTimerSchedule
            {
                Activated = response.PendingGlobalChargeTimer.Activated,
                Start = MapTime(response.PendingGlobalChargeTimer.Start),
                Stop = MapTime(response.PendingGlobalChargeTimer.Stop),
                SyncStatus = FormatSyncStatus(response.PendingGlobalChargeTimer.Metadata?.SyncStatus)
            };
        }

        return result;
    }

    private static ChargeTime? MapTime(ChronosProtos.DailyTime? dt)
    {
        if (dt == null) return null;
        return new ChargeTime
        {
            Hour = dt.Hour,
            Minute = dt.Minute,
            TimezoneOffsetMinutes = dt.TimeZone?.OffsetMinutes ?? 0
        };
    }

    private static string? FormatSyncStatus(ChronosProtos.SyncStatus? ss)
    {
        if (ss == null) return null;
        var statusStr = ss.Status switch
        {
            ChronosProtos.Status.Sent => "Sent",
            ChronosProtos.Status.Delivered => "Delivered",
            ChronosProtos.Status.Success => "Success",
            ChronosProtos.Status.Synced => "Synced",
            ChronosProtos.Status.CarOffline => "CarOffline",
            ChronosProtos.Status.CarError => "CarError",
            ChronosProtos.Status.Error => "Error",
            ChronosProtos.Status.Replaced => "Replaced",
            _ => "Unknown"
        };
        return string.IsNullOrEmpty(ss.Message) ? statusStr : $"{statusStr}: {ss.Message}";
    }
}
