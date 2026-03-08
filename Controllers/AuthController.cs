using Microsoft.AspNetCore.Mvc;
using PolestarApi.Models;
using PolestarApi.Services;

namespace PolestarApi.Controllers;

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
    /// Authenticate with Polestar ID and receive an access token.
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
}
