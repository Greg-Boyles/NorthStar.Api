namespace NorthStar.Api.Interfaces;

public interface ILockService
{
    Task<bool> AcquireLockAsync(string lockKey, string lockValue, TimeSpan expiry);
    Task<bool> RenewLockAsync(string lockKey, string lockValue, TimeSpan expiry);
    Task<bool> ReleaseLockAsync(string lockKey, string lockValue);
}
