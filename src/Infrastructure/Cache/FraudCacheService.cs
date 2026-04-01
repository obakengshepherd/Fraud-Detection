using System.Text.Json;
using StackExchange.Redis;
using FraudDetection.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FraudDetection.Infrastructure.Cache;

public class FraudCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<FraudCacheService> _logger;
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(30);

    public FraudCacheService(IConnectionMultiplexer redis, ILogger<FraudCacheService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<UserProfileRecord?> GetProfileAsync(string userId)
    {
        try
        {
            var val = await _db.StringGetAsync($"profile:{userId}");
            return val.HasValue ? JsonSerializer.Deserialize<UserProfileRecord>(val.ToString()) : null;
        }
        catch (Exception ex) 
        { 
            _logger.LogWarning(ex, "Redis profile read failed"); 
            return null; 
        }
    }

    public async Task SetProfileAsync(UserProfileRecord profile)
    {
        try
        {
            await _db.StringSetAsync(
                $"profile:{profile.UserId}",
                JsonSerializer.Serialize(profile), 
                ProfileTtl);
        }
        catch (Exception ex) 
        { 
            _logger.LogWarning(ex, "Redis profile write failed"); 
        }
    }

    /// <summary>
    /// Redis sliding window velocity counter using INCR + EXPIRE.
    /// Returns the count of events in the window.
    /// </summary>
    public async Task<long> IncrementVelocityAsync(string userId, string windowKey, int windowSeconds)
    {
        var key = $"velocity:{userId}:{windowKey}";
        try
        {
            var count = await _db.StringIncrementAsync(key);
            if (count == 1) // first event in this window — set expiry
                await _db.KeyExpireAsync(key, TimeSpan.FromSeconds(windowSeconds));
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis velocity increment failed — treating as 0");
            return 0;
        }
    }
}
