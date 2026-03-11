using NorthStar.Api.Models;

namespace NorthStar.Api.Interfaces;

public interface IChargingScheduleService
{
    Task<ChargingSchedule> GetChargingScheduleAsync(string accessToken, string vin, CancellationToken ct = default);
}
