using NorthStar.Api.Models;
using BatteryProtos = NorthStar.Api.Protos.Battery;
using OdometerProtos = NorthStar.Api.Protos;

namespace NorthStar.Api.Interfaces;

public interface ITripService
{
    Task<TripData> GetTripDataAsync(string accessToken, string vin, CancellationToken ct = default);
    Task<BatteryData> GetBatteryAsync(string accessToken, string vin, CancellationToken ct = default);
    BatteryData MapBatteryData(string vin, BatteryProtos.Battery battery);
    TripData? MapTripData(string vin, OdometerProtos.Odometer? odo, BatteryProtos.Battery? battery);
}
