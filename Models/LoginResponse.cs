namespace PolestarApi.Models;

public class LoginResponse
{
    public required string AccessToken { get; set; }
    public int ExpiresIn { get; set; }
    public string? TokenType { get; set; }
}
