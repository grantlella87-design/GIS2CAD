using System.Text.Json.Serialization;

namespace NG.GIS.CAD.Exporter.Services;

public sealed class ArcGisTokenProvider
{
    private const string ProductionClientId = "48XCGWtLoUxA3klq";
    private static readonly string PortalSharingRoot = "https" + "://" + "gis.nationalgrid.com" + "/portal/sharing/rest";
    private static readonly string RedirectUri = "http" + "://localhost:8080/";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArcGisTokenProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, "NationalGrid", "GisCadExporter");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "arcgis-token-cache.json");
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken);

            if (cache != null && IsUsable(cache))
            {
                return cache.AccessToken;
            }

            if (cache != null && !string.IsNullOrWhiteSpace(cache.RefreshToken))
            {
                try
                {
                    var refreshed = await RefreshAsync(cache, cancellationToken);
                    await SaveCacheAsync(refreshed, cancellationToken);
                    return refreshed.AccessToken;
                }
                catch
                {
                }
            }

            var fresh = await InteractiveLoginAsync(cancellationToken);
            await SaveCacheAsync(fresh, cancellationToken);
            return fresh.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsUsable(ArcGisTokenCacheRecord cache)
    {
        if (string.IsNullOrWhiteSpace(cache.AccessToken))
        {
            return false;
        }

        return cache.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5);
    }

    private async Task<ArcGisTokenCacheRecord?> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_cachePath);
        return await JsonSerializer.DeserializeAsync<ArcGisTokenCacheRecord>(stream, JsonOptions, cancellationToken);
    }

    private async Task SaveCacheAsync(ArcGisTokenCacheRecord cache, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_cachePath);
        await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken);
    }

    private async Task<ArcGisTokenCacheRecord> InteractiveLoginAsync(CancellationToken cancellationToken)
    {
        var state = Guid.NewGuid().ToString("N");
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        var authorizeUrl = PortalSharingRoot + "/oauth2/authorize" +
            "?client_id=" + Uri.EscapeDataString(ProductionClientId) +
            "&response_type=code" +
            "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
            "&state=" + Uri.EscapeDataString(state) +
            "&expiration=20160";

        Process.Start(new ProcessStartInfo
        {
            FileName = authorizeUrl,
            UseShellExecute = true
        });

        var context = await listener.GetContextAsync();
        var request = context.Request;
        var code = request.QueryString["code"];
        var returnedState = request.QueryString["state"];
        var error = request.QueryString["error"];

        var responseHtml = "<html><body><h2>National Grid GIS CAD Exporter authentication complete.</h2><p>You can close this browser tab and return to AutoCAD.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        context.Response.OutputStream.Close();
        listener.Stop();

        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException("ArcGIS OAuth returned error: " + error);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("ArcGIS OAuth did not return an authorization code.");
        }

        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ArcGIS OAuth state mismatch.");
        }

        return await ExchangeCodeAsync(code, cancellationToken);
    }

    private async Task<ArcGisTokenCacheRecord> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var tokenUrl = PortalSharingRoot + "/oauth2/token";

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["client_id"] = ProductionClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri
        });

        using var response = await _httpClient.PostAsync(tokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var token = JsonSerializer.Deserialize<ArcGisOAuthTokenResponse>(body, JsonOptions);

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("ArcGIS OAuth token response did not contain an access token. Response: " + body);
        }

        if (!string.IsNullOrWhiteSpace(token.Error))
        {
            throw new InvalidOperationException("ArcGIS OAuth token error: " + body);
        }

        return new ArcGisTokenCacheRecord
        {
            PortalSharingRoot = PortalSharingRoot,
            ClientId = ProductionClientId,
            RedirectUri = RedirectUri,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken ?? string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
            Username = token.Username ?? string.Empty
        };
    }

    private async Task<ArcGisTokenCacheRecord> RefreshAsync(ArcGisTokenCacheRecord cache, CancellationToken cancellationToken)
    {
        var tokenUrl = PortalSharingRoot + "/oauth2/token";

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["client_id"] = ProductionClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = cache.RefreshToken
        });

        using var response = await _httpClient.PostAsync(tokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var token = JsonSerializer.Deserialize<ArcGisOAuthTokenResponse>(body, JsonOptions);

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("ArcGIS refresh response did not contain an access token. Response: " + body);
        }

        if (!string.IsNullOrWhiteSpace(token.Error))
        {
            throw new InvalidOperationException("ArcGIS refresh token error: " + body);
        }

        cache.AccessToken = token.AccessToken;

        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            cache.RefreshToken = token.RefreshToken;
        }

        cache.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));

        if (!string.IsNullOrWhiteSpace(token.Username))
        {
            cache.Username = token.Username;
        }

        return cache;
    }

    private sealed class ArcGisOAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; } = 1800;

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
