using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>
/// Browses CAD templates in SharePoint through Microsoft Graph.
///
/// Sign-in is deliberately manual. Only <see cref="SignInInteractiveAsync"/> can open a browser, and
/// nothing calls it except the sign-in button. Every other path acquires tokens silently and fails
/// with a message asking the user to sign in. An earlier version fell back to interactive sign-in
/// whenever a token was missing, which, driven from a UI hook that re-ran continuously, opened
/// browser tabs in a loop.
/// </summary>
public sealed class SharePointDwtTemplateService
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";
    private const string TokenCacheFileName = "graph-token-cache.bin";

    private static readonly string TokenCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NationalGrid", "GisCadExporter");

    private readonly SharePointTemplateSettings _settings;
    private readonly string[] _scopes;
    private readonly IPublicClientApplication _app;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _tokenCacheGate = new(1, 1);
    private bool _tokenCacheAttempted;

    public SharePointDwtTemplateService(SharePointTemplateSettings settings)
    {
        _settings = settings;
        _scopes = (settings.Scopes ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        if (_scopes.Length == 0) { _scopes = new[] { "User.Read", "Sites.ReadWrite.All" }; }

        _app = PublicClientApplicationBuilder
            .Create(settings.ClientId)
            .WithTenantId(settings.TenantId)
            .WithRedirectUri("http://localhost")
            .Build();
    }

    /// <summary>The signed-in account, or null when there is no usable session.</summary>
    public string? SignedInAs { get; private set; }

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(SignedInAs);

    /// <summary>
    /// Set only when the session could not be made persistent. Empty means the cache is working and
    /// there is nothing to tell the user about.
    /// </summary>
    public string TokenCacheWarning { get; private set; } = string.Empty;

    /// <summary>
    /// Backs MSAL's token cache with a file so a sign-in survives closing AutoCAD, instead of living
    /// only in this process. On Windows the file is encrypted with DPAPI for the current user, so the
    /// tokens are not readable by other accounts on the machine.
    ///
    /// This has to happen before any account or token call, otherwise MSAL answers from an empty
    /// in-memory cache and the user is asked to sign in again despite a saved session existing.
    /// A failure here is not fatal: sign-in still works for the session, it just will not persist.
    /// </summary>
    private async Task EnsureTokenCacheAttachedAsync()
    {
        if (_tokenCacheAttempted) { return; }

        await _tokenCacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_tokenCacheAttempted) { return; }

            // Set before the attempt so a failure is not retried on every subsequent call.
            _tokenCacheAttempted = true;

            try
            {
                Directory.CreateDirectory(TokenCacheDirectory);
                var properties = new StorageCreationPropertiesBuilder(TokenCacheFileName, TokenCacheDirectory).Build();
                var helper = await MsalCacheHelper.CreateAsync(properties, null).ConfigureAwait(false);
                helper.RegisterCache(_app.UserTokenCache);
                TokenCacheWarning = string.Empty;
            }
            catch (Exception ex)
            {
                TokenCacheWarning = "This sign-in will not be remembered after AutoCAD closes: " + ex.Message;
            }
        }
        finally { _tokenCacheGate.Release(); }
    }

    /// <summary>
    /// Restores a session from MSAL's cache without any UI. Returns false when interactive sign-in
    /// would be required, which is the caller's cue to leave the user signed out rather than prompt.
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync()
    {
        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);
        try
        {
            var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account == null) { return false; }

            var result = await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync().ConfigureAwait(false);
            SignedInAs = result.Account?.Username;
            return IsSignedIn;
        }
        catch (MsalUiRequiredException) { return false; }
        catch (MsalException) { return false; }
    }

    /// <summary>The only method that may open a browser. Call it from an explicit user action.</summary>
    public async Task SignInInteractiveAsync(CancellationToken cancellationToken)
    {
        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);

        var result = await _app
            .AcquireTokenInteractive(_scopes)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        SignedInAs = result.Account?.Username;
    }

    public async Task SignOutAsync()
    {
        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);

        foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
        {
            await _app.RemoveAsync(account).ConfigureAwait(false);
        }
        SignedInAs = null;
    }

    public async Task<IReadOnlyList<SharePointDwtTemplateItem>> ListTemplatesAsync(CancellationToken cancellationToken)
    {
        var token = await AcquireTokenSilentlyAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<SharePointDwtTemplateItem>();
        await AddTemplatesFromFolderAsync(token, _settings.DriveId, _settings.FolderPath, found, 0, cancellationToken).ConfigureAwait(false);
        return found.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Downloads a template next to the other cached templates and returns its local path.</summary>
    public async Task<string> DownloadTemplateAsync(SharePointDwtTemplateItem item, CancellationToken cancellationToken)
    {
        var token = await AcquireTokenSilentlyAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            GraphRoot + "/drives/" + Uri.EscapeDataString(item.DriveId) + "/items/" + Uri.EscapeDataString(item.ItemId) + "/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NationalGrid", "GisCadExporter", "Templates");
        Directory.CreateDirectory(directory);

        var safeName = string.Concat(item.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (!safeName.EndsWith(".dwt", StringComparison.OrdinalIgnoreCase)) { safeName += ".dwt"; }

        var path = Path.Combine(directory, safeName);
        await using (var file = File.Create(path))
        {
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        return path;
    }

    public static void OpenTemplatePath(string path)
    {
        if (!File.Exists(path)) { throw new FileNotFoundException("The downloaded template was not found.", path); }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// Silent only, by design. A missing or expired session surfaces as an instruction to sign in
    /// rather than as a browser window the user did not ask for.
    /// </summary>
    private async Task<string> AcquireTokenSilentlyAsync(CancellationToken cancellationToken)
    {
        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);

        var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (account == null)
        {
            SignedInAs = null;
            throw new InvalidOperationException("Not signed in to SharePoint. Use Sign in to SharePoint first.");
        }

        try
        {
            var result = await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            SignedInAs = result.Account?.Username;
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            SignedInAs = null;
            throw new InvalidOperationException("The SharePoint session expired. Use Sign in to SharePoint again.");
        }
    }

    private async Task AddTemplatesFromFolderAsync(
        string token, string driveId, string folderPath, List<SharePointDwtTemplateItem> found, int depth, CancellationToken cancellationToken)
    {
        // Template libraries are shallow; the cap only stops a pathological tree from spinning.
        if (depth > 5) { return; }

        var endpoint = "/drives/" + Uri.EscapeDataString(driveId) + "/root:/" + EscapePath(folderPath) + ":/children";
        using var document = await GetJsonAsync(token, endpoint, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("value", out var children)) { return; }

        foreach (var child in children.EnumerateArray())
        {
            var name = child.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;

            if (child.TryGetProperty("folder", out _))
            {
                await AddTemplatesFromFolderAsync(token, driveId, folderPath.TrimEnd('/') + "/" + name, found, depth + 1, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!name.EndsWith(".dwt", StringComparison.OrdinalIgnoreCase)) { continue; }

            found.Add(new SharePointDwtTemplateItem(
                driveId,
                child.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                name,
                child.TryGetProperty("webUrl", out var webUrl) ? webUrl.GetString() ?? string.Empty : string.Empty));
        }
    }

    private static string EscapePath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private async Task<JsonDocument> GetJsonAsync(string token, string graphPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GraphRoot + graphPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("SharePoint request failed (" + (int)response.StatusCode + " " + response.StatusCode + "). " + SummarizeGraphError(text));
        }
        return JsonDocument.Parse(text);
    }

    /// <summary>Graph returns its reason in error.message; the rest of the payload is noise.</summary>
    private static string SummarizeGraphError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}

public sealed record SharePointDwtTemplateItem(string DriveId, string ItemId, string Name, string WebUrl)
{
    public override string ToString() => Name;
}
