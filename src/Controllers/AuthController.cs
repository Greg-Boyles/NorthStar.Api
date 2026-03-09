using Microsoft.AspNetCore.Mvc;
using NorthStar.Api.Models;
using NorthStar.Api.Services;

namespace NorthStar.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly PolestarAuthService _authService;

    public AuthController(PolestarAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Authenticate with Polestar ID and receive an access token + refresh token.
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        try
        {
            var result = await _authService.AuthenticateAsync(request.Email, request.Password);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = "Failed to reach Polestar authentication servers", detail = ex.Message });
        }
    }

    /// <summary>
    /// Refresh an access token using a refresh token. Single HTTP call vs full OIDC login.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<LoginResponse>> Refresh([FromBody] RefreshRequest request)
    {
        try
        {
            var result = await _authService.RefreshTokenAsync(request.RefreshToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = "Failed to reach Polestar authentication servers", detail = ex.Message });
        }
    }
}
