using NorthStar.Api.Models;

namespace NorthStar.Api.Interfaces;

public interface IAuthService
{
    Task<LoginResponse> AuthenticateAsync(string email, string password);
    Task<LoginResponse> RefreshTokenAsync(string refreshToken);
}
