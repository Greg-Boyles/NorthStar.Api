using Microsoft.AspNetCore.Mvc;
using NorthStar.Api.Interfaces;
using NorthStar.Api.Models;

namespace NorthStar.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CarsController : ControllerBase
{
    private readonly ICarService _carService;
    private readonly ITripService _tripService;
    private readonly IStatusService _statusService;
    private readonly IChargingScheduleService _chargingScheduleService;
    private readonly IClimateScheduleService _climateScheduleService;
    private readonly ISnapshotService _snapshotService;

    public CarsController(ICarService carService, ITripService tripService, IStatusService statusService, IChargingScheduleService chargingScheduleService, IClimateScheduleService climateScheduleService, ISnapshotService snapshotService)
    {
        _carService = carService;
        _tripService = tripService;
        _statusService = statusService;
        _chargingScheduleService = chargingScheduleService;
        _climateScheduleService = climateScheduleService;
        _snapshotService = snapshotService;
    }

    /// <summary>
    /// Get all cars for the authenticated user.
    /// Requires Bearer token from /api/auth/login in the Authorization header.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<Car>>> GetCars()
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var cars = await _carService.GetCarsAsync(token);
            return Ok(cars);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = "Failed to reach Polestar API", detail = ex.Message });
        }
    }

    /// <summary>
    /// Get battery/charging status for a specific car.
    /// Requires Bearer token from /api/auth/login in the Authorization header.
    /// </summary>
    [HttpGet("{vin}/battery")]
    public async Task<ActionResult<BatteryData>> GetBattery(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var batteryData = await _tripService.GetBatteryAsync(token, vin, ct);
            return Ok(batteryData);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Grpc.Core.RpcException ex)
        {
            return StatusCode(502, new { error = "gRPC call to vehicle service failed", detail = ex.Status.Detail, code = ex.StatusCode.ToString() });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { error = "Timeout waiting for vehicle data (car may be asleep)" });
        }
    }

    /// <summary>
    /// Get trip/odometer data for a specific car.
    /// Requires Bearer token from /api/auth/login in the Authorization header.
    /// </summary>
    [HttpGet("{vin}/trips")]
    public async Task<ActionResult<TripData>> GetTrips(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var tripData = await _tripService.GetTripDataAsync(token, vin, ct);
            return Ok(tripData);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Grpc.Core.RpcException ex)
        {
            return StatusCode(502, new { error = "gRPC call to vehicle service failed", detail = ex.Status.Detail, code = ex.StatusCode.ToString() });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { error = "Timeout waiting for vehicle data (car may be asleep)" });
        }
    }

    /// <summary>
    /// Get comprehensive vehicle status: locks, doors, windows, availability, climate, battery, health.
    /// Requires Bearer token from /api/auth/login in the Authorization header.
    /// </summary>
    [HttpGet("{vin}/status")]
    public async Task<ActionResult<VehicleStatus>> GetStatus(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var status = await _statusService.GetVehicleStatusAsync(token, vin, ct);
            return Ok(status);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return StatusCode(502, new { error = "gRPC call to vehicle service failed", detail = ex.Status.Detail, code = ex.StatusCode.ToString() });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { error = "Timeout waiting for vehicle data (car may be asleep)" });
        }
    }

    /// <summary>
    /// Get charging schedule (global charge timer) for a specific car.
    /// Requires Bearer token from /api/auth/login in the Authorization header.
    /// </summary>
    [HttpGet("{vin}/charging-schedule")]
    public async Task<ActionResult<ChargingSchedule>> GetChargingSchedule(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var schedule = await _chargingScheduleService.GetChargingScheduleAsync(token, vin, ct);
            return Ok(schedule);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Grpc.Core.RpcException ex)
        {
            return StatusCode(502, new { error = "gRPC call to PCCS charging service failed", detail = ex.Status.Detail, code = ex.StatusCode.ToString() });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { error = "Timeout waiting for charging schedule data" });
        }
    }

    /// <summary>
    /// Get climate schedule (parking climatization timers and settings) for a specific car.
    /// Requires Bearer token from /api/auth/login in the Authorization header.
    /// </summary>
    [HttpGet("{vin}/climate-schedule")]
    public async Task<ActionResult<ClimateSchedule>> GetClimateSchedule(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var schedule = await _climateScheduleService.GetClimateScheduleAsync(token, vin, ct);
            return Ok(schedule);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return StatusCode(502, new { error = "gRPC call to PCCS climate service failed", detail = ex.Status.Detail, code = ex.StatusCode.ToString() });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { error = "Timeout waiting for climate schedule data" });
        }
    }

    /// <summary>
    /// Get a unified snapshot of all vehicle data in a single request.
    /// Tries cache first (X-Data-Source: cache), falls back to live calls (X-Data-Source: live).
    /// Reduces upstream API calls by sharing gRPC channels and deduplicating battery queries.
    /// </summary>
    [HttpGet("{vin}/snapshot")]
    public async Task<ActionResult<VehicleSnapshot>> GetSnapshot(string vin, CancellationToken ct)
    {
        var token = ExtractBearerToken();
        if (token == null)
            return Unauthorized(new { error = "Missing or invalid Authorization header. Use: Bearer <token>" });

        try
        {
            var snapshot = await _snapshotService.GetSnapshotAsync(token, vin, ct);
            
            // Add X-Data-Source header to indicate if data came from cache or live upstream calls
            // VehicleSnapshotService logs whether cache was hit or missed
            Response.Headers.Append("X-Data-Source", snapshot.Timestamp > DateTime.UtcNow.AddMinutes(-5) ? "cache" : "live");
            
            return Ok(snapshot);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return StatusCode(502, new { error = "gRPC call to vehicle service failed", detail = ex.Status.Detail, code = ex.StatusCode.ToString() });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { error = "Timeout waiting for vehicle data (car may be asleep)" });
        }
    }

    private string? ExtractBearerToken()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return authHeader["Bearer ".Length..].Trim();
    }
}
