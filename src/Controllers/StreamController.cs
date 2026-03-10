using Microsoft.AspNetCore.Mvc;
using NorthStar.Api.Models;
using NorthStar.Api.Services;

namespace NorthStar.Api.Controllers;

[ApiController]
[Route("api/stream")]
public class StreamController : ControllerBase
{
    private readonly VehicleStateCache _cache;
    private readonly PolestarAuthService _authService;
    private readonly VehicleStreamService _streamService;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        VehicleStateCache cache,
        PolestarAuthService authService,
        VehicleStreamService streamService,
        ILogger<StreamController> logger)
    {
        _cache = cache;
        _authService = authService;
        _streamService = streamService;
        _logger = logger;
    }

    /// <summary>
    /// Start streaming vehicle data for a VIN. Stores refresh token and begins background gRPC streams.
    /// Called by HA integration on setup.
    /// </summary>
    [HttpPost("{vin}/start")]
    public async Task<IActionResult> StartStream(string vin, [FromBody] StartStreamRequest request)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return BadRequest(new { error = "RefreshToken is required" });

        try
        {
            // Validate refresh token by attempting to use it
            _logger.LogInformation("Starting stream for VIN {Vin}", vin);
            var tokenResponse = await _authService.RefreshTokenAsync(request.RefreshToken);

            if (tokenResponse.RefreshToken is null)
            {
                return BadRequest(new { error = "Invalid refresh token or token rotation not supported" });
            }
            
            // Store refresh token and initialize lastAccess timestamp
            await _cache.SetRefreshTokenAsync(vin, tokenResponse.RefreshToken);
            await _cache.UpdateLastAccessAsync(vin);

            // Start background gRPC streams
            await _streamService.StartStreamsForVinAsync(vin, tokenResponse.RefreshToken, HttpContext.RequestAborted);

            _logger.LogInformation("Stream started for VIN {Vin}", vin);

            return Ok(new
            {
                message = "Stream started successfully",
                vin = vin,
                status = "active"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start stream for VIN {Vin}", vin);
            return StatusCode(502, new { error = "Failed to validate refresh token", detail = ex.Message });
        }
    }

    /// <summary>
    /// Get cached stream data for a VIN. Returns 404 if no active streams.
    /// Updates lastAccess timestamp to prevent auto-cleanup.
    /// </summary>
    [HttpGet("{vin}")]
    public async Task<ActionResult<VehicleSnapshot>> GetStreamData(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        // Check if streams are active (refresh token exists)
        var refreshToken = await _cache.GetRefreshTokenAsync(vin, ct);
        if (refreshToken == null)
        {
            _logger.LogWarning("No active streams for VIN {Vin}", vin);
            return NotFound(new
            {
                error = "No active streams for this VIN",
                message = "Call POST /api/stream/{vin}/start to enable streaming",
                vin = vin
            });
        }

        // Get cached snapshot
        var snapshot = await _cache.GetSnapshotAsync(vin, ct);
        if (snapshot == null)
        {
            _logger.LogWarning("Stream active but cache empty for VIN {Vin}", vin);
            return NotFound(new
            {
                error = "Stream active but no data cached yet",
                message = "Streams may still be initializing. Try again in a few seconds.",
                vin = vin
            });
        }

        // Update lastAccess to prevent auto-cleanup
        await _cache.UpdateLastAccessAsync(vin, ct);

        // Add ETag based on snapshot timestamp
        var etag = $"\"{snapshot.Timestamp:O}\"";
        Response.Headers.Append("ETag", etag);

        // Check If-None-Match for 304 support
        var ifNoneMatch = Request.Headers.IfNoneMatch.FirstOrDefault();
        if (ifNoneMatch == etag)
        {
            _logger.LogDebug("ETag match for VIN {Vin}, returning 304", vin);
            return StatusCode(304);
        }

        return Ok(snapshot);
    }

    /// <summary>
    /// Stop streaming for a VIN. Removes refresh token and cached data.
    /// Called by HA integration on removal (optional - auto-cleanup handles this too).
    /// </summary>
    [HttpPost("{vin}/stop")]
    public async Task<IActionResult> StopStream(string vin)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        _logger.LogInformation("Stopping stream for VIN {Vin}", vin);

        // Remove all cached data and tokens
        await _cache.RemoveAsync(vin);

        return Ok(new
        {
            message = "Stream stopped successfully",
            vin = vin,
            status = "stopped"
        });
    }

    private string? ExtractBearerToken()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return authHeader["Bearer ".Length..].Trim();
    }
}

public class StartStreamRequest
{
    public required string RefreshToken { get; set; }
}
