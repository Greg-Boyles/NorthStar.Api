using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Models;
using BatteryProtos = NorthStar.Api.Protos.Battery;

namespace NorthStar.Api.Interfaces;

public interface IStatusService
{
    Task<VehicleStatus> GetVehicleStatusAsync(string accessToken, string vin, CancellationToken ct = default);
    Task<ExteriorStatus?> GetExteriorAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct);
    Task<AvailabilityInfo?> GetAvailabilityAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct);
    Task<ClimateStatus?> GetClimateAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct);
    Task<BatteryStatus?> GetBatteryAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct);
    Task<HealthStatus?> GetHealthAsync(string accessToken, string vin, CancellationToken ct);
    BatteryStatus? MapBatteryStatus(BatteryProtos.Battery? b);
}
