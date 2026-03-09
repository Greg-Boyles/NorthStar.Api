using System.Text.Json.Serialization;

namespace NorthStar.Api.Models;

/// <summary>
/// Raw OIDC token endpoint response from Polestar's PingFederate provider.
/// </summary>
public class OidcTokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}
