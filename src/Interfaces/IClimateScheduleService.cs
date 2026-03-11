using NorthStar.Api.Models;

namespace NorthStar.Api.Interfaces;

public interface IClimateScheduleService
{
    Task<ClimateSchedule> GetClimateScheduleAsync(string accessToken, string vin, CancellationToken ct = default);
}
