using NorthStar.Api.Models;

namespace NorthStar.Api.Interfaces;

public interface IVehicleStateCache
{
    Task<VehicleSnapshot?> GetSnapshotAsync(string vin, CancellationToken ct = default);
    Task SetAsync<T>(string vin, string key, T value, CancellationToken ct = default);
    Task SetSnapshotAsync(string vin, VehicleSnapshot snapshot, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLastAccessAsync(string vin, CancellationToken ct = default);
    Task UpdateLastAccessAsync(string vin, CancellationToken ct = default);
    Task SetRefreshTokenAsync(string vin, string refreshToken, CancellationToken ct = default);
    Task<string?> GetRefreshTokenAsync(string vin, CancellationToken ct = default);
    Task RemoveAsync(string vin, CancellationToken ct = default);
}
