using StackExchange.Redis;

namespace NorthStar.Api.Services;

/// <summary>
/// Service for managing distributed locks using Redis.
/// Ensures only one task/instance can acquire a lock for a given resource.
/// </summary>
public class RedisLockService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisLockService> _logger;

    public RedisLockService(IConnectionMultiplexer redis, ILogger<RedisLockService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Attempt to acquire a distributed lock for a resource.
    /// </summary>
    /// <param name="lockKey">The Redis key for the lock</param>
    /// <param name="lockValue">Unique identifier for this lock (e.g. GUID)</param>
    /// <param name="expiry">How long the lock should be held before auto-expiring</param>
    /// <returns>True if lock was acquired, false if already held by another process</returns>
    public async Task<bool> AcquireLockAsync(string lockKey, string lockValue, TimeSpan expiry)
    {
        try
        {
            var db = _redis.GetDatabase();
            var acquired = await db.StringSetAsync(lockKey, lockValue, expiry, When.NotExists);
            
            if (acquired)
            {
                _logger.LogDebug("Acquired lock: {LockKey}", lockKey);
            }
            else
            {
                _logger.LogDebug("Failed to acquire lock (already held): {LockKey}", lockKey);
            }

            return acquired;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring lock: {LockKey}", lockKey);
            return false;
        }
    }

    /// <summary>
    /// Renew the expiry time of a lock if we still own it.
    /// </summary>
    /// <param name="lockKey">The Redis key for the lock</param>
    /// <param name="lockValue">Our unique lock identifier to verify ownership</param>
    /// <param name="expiry">New expiry duration</param>
    /// <returns>True if renewal succeeded, false if we no longer own the lock</returns>
    public async Task<bool> RenewLockAsync(string lockKey, string lockValue, TimeSpan expiry)
    {
        try
        {
            var db = _redis.GetDatabase();
            
            // Only renew if we still own the lock
            var currentValue = await db.StringGetAsync(lockKey);
            if (currentValue == lockValue)
            {
                await db.KeyExpireAsync(lockKey, expiry);
                _logger.LogDebug("Renewed lock: {LockKey}", lockKey);
                return true;
            }

            _logger.LogWarning("Failed to renew lock (ownership lost): {LockKey}", lockKey);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing lock: {LockKey}", lockKey);
            return false;
        }
    }

    /// <summary>
    /// Release a lock if we still own it.
    /// </summary>
    /// <param name="lockKey">The Redis key for the lock</param>
    /// <param name="lockValue">Our unique lock identifier to verify ownership</param>
    /// <returns>True if lock was released, false if we didn't own it</returns>
    public async Task<bool> ReleaseLockAsync(string lockKey, string lockValue)
    {
        try
        {
            var db = _redis.GetDatabase();
            
            // Only release if we still own the lock
            var currentValue = await db.StringGetAsync(lockKey);
            if (currentValue == lockValue)
            {
                await db.KeyDeleteAsync(lockKey);
                _logger.LogDebug("Released lock: {LockKey}", lockKey);
                return true;
            }

            _logger.LogDebug("Lock already released or owned by another process: {LockKey}", lockKey);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing lock: {LockKey}", lockKey);
            return false;
        }
    }
}
