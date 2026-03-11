namespace NorthStar.Api.Interfaces;

public interface IStreamService
{
    Task StartStreamsForVinAsync(string vin, string refreshToken, CancellationToken ct);
}
