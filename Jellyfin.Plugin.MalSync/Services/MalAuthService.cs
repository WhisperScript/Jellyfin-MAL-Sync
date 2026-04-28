using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MalSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MalSync.Services;

/// <summary>Handles MAL OAuth 2.0 PKCE flow and automatic token refresh.</summary>
public sealed class MalAuthService
{
    private const string TokenEndpoint = "https://myanimelist.net/v1/oauth2/token";
    private const string AuthEndpoint = "https://myanimelist.net/v1/oauth2/authorize";
    private const string RedirectUri = "http://localhost";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MalAuthService> _logger;

    // Keyed by Jellyfin userId – lives only during the active OAuth flow
    private readonly Dictionary<string, string> _pendingVerifiers = new();

    public MalAuthService(IHttpClientFactory httpFactory, ILogger<MalAuthService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // ── Step 1: Build authorization URL ───────────────────────────────────

    /// <summary>
    /// Creates a PKCE code-verifier, stores it for <paramref name="userId"/>
    /// and returns the MAL authorization URL the user must open in a browser.
    /// </summary>
    public string GetAuthorizationUrl(string userId, string clientId)
    {
        var verifier = GenerateCodeVerifier();
        _pendingVerifiers[userId] = verifier;

        return $"{AuthEndpoint}?response_type=code" +
               $"&client_id={Uri.EscapeDataString(clientId)}" +
               $"&code_challenge={Uri.EscapeDataString(verifier)}" +
               $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";
    }

    // ── Step 2: Exchange authorization code ───────────────────────────────

    /// <summary>
    /// Exchanges the authorization <paramref name="code"/> (from the redirect
    /// URL) for an access/refresh token pair and stores it for
    /// <paramref name="userId"/>.
    /// </summary>
    public async Task<(bool Success, string Message)> ExchangeCodeAsync(
        string userId, string clientId, string code)
    {
        if (!_pendingVerifiers.TryGetValue(userId, out var verifier))
            return (false, "No pending authorization found – please start the auth flow again.");

        _pendingVerifiers.Remove(userId);

        using var http = _httpFactory.CreateClient("MalSync");
        var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUri,
            })).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogWarning("MAL token exchange failed ({Status}): {Body}", response.StatusCode, body);
            return (false, $"MAL returned HTTP {(int)response.StatusCode}: {body}");
        }

        var data = await response.Content.ReadFromJsonAsync<MalTokenResponse>().ConfigureAwait(false);
        if (data is null || string.IsNullOrEmpty(data.AccessToken))
            return (false, "MAL response did not contain an access_token.");

        SaveToken(userId, data);
        return (true, "Authorization successful.");
    }

    // ── Token refresh ─────────────────────────────────────────────────────

    /// <summary>
    /// Uses the stored refresh token to silently obtain a new access token.
    /// Called automatically by the sync job when the token is close to expiry.
    /// </summary>
    public async Task<bool> RefreshTokenAsync(string userId)
    {
        var config = MalSyncPlugin.Instance!.Configuration;
        var userCfg = GetOrCreateUserConfig(userId);
        var clientId = config.MalClientId;

        if (string.IsNullOrEmpty(userCfg.MalRefreshToken))
        {
            _logger.LogWarning("MAL refresh: no refresh token stored for user {UserId}", userId);
            return false;
        }

        using var http = _httpFactory.CreateClient("MalSync");
        var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = userCfg.MalRefreshToken,
            })).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogWarning("MAL token refresh failed ({Status}): {Body}", response.StatusCode, body);
            return false;
        }

        var data = await response.Content.ReadFromJsonAsync<MalTokenResponse>().ConfigureAwait(false);
        if (data is null || string.IsNullOrEmpty(data.AccessToken))
            return false;

        SaveToken(userId, data);
        _logger.LogInformation("MAL access token refreshed for user {UserId}", userId);
        return true;
    }

    // ── Public helpers ────────────────────────────────────────────────────

    public bool HasValidToken(string userId)
    {
        var uc = GetOrCreateUserConfig(userId);
        return !string.IsNullOrEmpty(uc.MalAccessToken)
            && uc.TokenExpiresAt > DateTime.UtcNow.AddMinutes(5);
    }

    /// <summary>
    /// Returns the current access token, refreshing it transparently when
    /// less than 5 minutes remain before expiry.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string userId)
    {
        var uc = GetOrCreateUserConfig(userId);
        if (string.IsNullOrEmpty(uc.MalAccessToken))
            return null;

        if (uc.TokenExpiresAt < DateTime.UtcNow.AddMinutes(5))
        {
            if (!await RefreshTokenAsync(userId).ConfigureAwait(false))
                return null;
            uc = GetOrCreateUserConfig(userId);
        }

        return uc.MalAccessToken;
    }

    public UserMalConfig GetOrCreateUserConfig(string userId)
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;
        var uc = cfg.UserConfigs.FirstOrDefault(u => u.UserId == userId);
        if (uc is null)
        {
            uc = new UserMalConfig { UserId = userId };
            cfg.UserConfigs.Add(uc);
        }
        return uc;
    }

    // ── Private ───────────────────────────────────────────────────────────

    private static void SaveToken(string userId, MalTokenResponse data)
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;
        var uc = cfg.UserConfigs.FirstOrDefault(u => u.UserId == userId);
        if (uc is null)
        {
            uc = new UserMalConfig { UserId = userId };
            cfg.UserConfigs.Add(uc);
        }

        uc.MalAccessToken = data.AccessToken;
        uc.MalRefreshToken = string.IsNullOrEmpty(data.RefreshToken)
                             ? uc.MalRefreshToken
                             : data.RefreshToken;
        uc.TokenExpiresAt = DateTime.UtcNow.AddSeconds(
                             data.ExpiresIn > 0 ? data.ExpiresIn : 2_592_000);

        MalSyncPlugin.Instance.SaveConfiguration();
    }

    private static string GenerateCodeVerifier()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(100);
        var sb = new StringBuilder(100);
        foreach (var b in bytes)
            sb.Append(Alphabet[b % Alphabet.Length]);
        return sb.ToString();
    }

    private sealed class MalTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
