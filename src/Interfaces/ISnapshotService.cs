using NorthStar.Api.Models;

namespace NorthStar.Api.Interfaces;

public interface ISnapshotService
{
    Task<VehicleSnapshot> GetSnapshotAsync(string accessToken, string vin, CancellationToken ct = default);
}
