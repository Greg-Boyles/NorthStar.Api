using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NorthStar.Api.Models;

namespace NorthStar.Api.Services;

public class PolestarAuthService
{
    private const string OidcProviderBaseUrl = "https://polestarid.eu.polestar.com";
    private const string OidcClientId = "l3oopkc_10";
    private const string OidcRedirectUri = "https://www.polestar.com/sign-in-callback";
    private const string OidcScope = "openid profile email customer:attributes";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PolestarAuthService> _logger;

    public PolestarAuthService(IHttpClientFactory httpClientFactory, ILogger<PolestarAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<LoginResponse> AuthenticateAsync(string email, string password)
    {
        // We need a handler that doesn't auto-redirect and tracks cookies
        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookieContainer,
            UseCookies = true
        };
        using var client = new HttpClient(handler);

        // Step 1: Fetch OIDC configuration
        _logger.LogInformation("Fetching OIDC configuration");
        var oidcResp = await client.GetAsync($"{OidcProviderBaseUrl}/.well-known/openid-configuration");
        oidcResp.EnsureSuccessStatusCode();
        var oidcDoc = await JsonDocument.ParseAsync(await oidcResp.Content.ReadAsStreamAsync());
        var authorizationEndpoint = oidcDoc.RootElement.GetProperty("authorization_endpoint").GetString()!;
        var tokenEndpoint = oidcDoc.RootElement.GetProperty("token_endpoint").GetString()!;

        // Step 2: Generate PKCE values
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        // Step 3: Get resume path (allow redirects for this step)
        _logger.LogInformation("Getting resume path");
        var authParams = BuildAuthParams(state, codeChallenge);
        var authUrl = $"{authorizationEndpoint}?{authParams}";

        // Need to follow redirects for resume path - use a separate handler
        using var followHandler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookieContainer,
            UseCookies = true
        };
        using var followClient = new HttpClient(followHandler);
        var resumeResp = await followClient.GetAsync(authUrl);
        var resumeHtml = await resumeResp.Content.ReadAsStringAsync();

        var resumeMatch = Regex.Match(resumeHtml, @"(?:url|action):\s*""(.+?)""");
        if (!resumeMatch.Success)
            throw new InvalidOperationException("Could not find resume path in login page");
        var resumePath = resumeMatch.Groups[1].Value;

        // Step 4: Submit credentials
        _logger.LogInformation("Submitting credentials");
        var loginUrl = $"{OidcProviderBaseUrl}{resumePath}?{authParams}";
        var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pf.username"] = email,
            ["pf.pass"] = password
        });
        var loginResp = await client.PostAsync(loginUrl, loginContent);

        if ((int)loginResp.StatusCode is not (302 or 303))
            throw new InvalidOperationException($"Login failed with status {loginResp.StatusCode}");

        var location = loginResp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location))
            throw new InvalidOperationException("No redirect location after login");

        // Extract auth code from redirect
        var redirectUri = new Uri(location, UriKind.RelativeOrAbsolute);
        if (!redirectUri.IsAbsoluteUri)
            redirectUri = new Uri(new Uri(OidcProviderBaseUrl), location);

        var queryParams = System.Web.HttpUtility.ParseQueryString(redirectUri.Query);
        var code = queryParams["code"];

        // Handle terms/conditions acceptance if needed
        if (string.IsNullOrEmpty(code))
        {
            var uid = queryParams["uid"];
            if (!string.IsNullOrEmpty(uid))
            {
                _logger.LogInformation("Accepting terms/conditions");
                var confirmContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["pf.submit"] = "true",
                    ["subject"] = uid
                });
                var confirmResp = await client.PostAsync(loginUrl, confirmContent);
                var confirmLocation = confirmResp.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(confirmLocation))
                {
                    var confirmUri = new Uri(confirmLocation, UriKind.RelativeOrAbsolute);
                    if (!confirmUri.IsAbsoluteUri)
                        confirmUri = new Uri(new Uri(OidcProviderBaseUrl), confirmLocation);
                    code = System.Web.HttpUtility.ParseQueryString(confirmUri.Query)["code"];
                }
            }
        }

        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Failed to obtain authorization code");

        // Step 5: Exchange code for tokens
        _logger.LogInformation("Exchanging code for tokens");
        var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = OidcClientId,
            ["code"] = code,
            ["redirect_uri"] = OidcRedirectUri,
            ["code_verifier"] = codeVerifier
        });
        var tokenResp = await client.PostAsync(tokenEndpoint, tokenContent);
        tokenResp.EnsureSuccessStatusCode();

        var tokenDoc = await JsonDocument.ParseAsync(await tokenResp.Content.ReadAsStreamAsync());
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = tokenDoc.RootElement.GetProperty("expires_in").GetInt32();

        return new LoginResponse
        {
            AccessToken = accessToken,
            ExpiresIn = expiresIn,
            TokenType = "Bearer"
        };
    }

    private static string BuildAuthParams(string state, string codeChallenge)
    {
        var p = new Dictionary<string, string>
        {
            ["client_id"] = OidcClientId,
            ["redirect_uri"] = OidcRedirectUri,
            ["response_type"] = "code",
            ["scope"] = OidcScope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return string.Join("&", p.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
