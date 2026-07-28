namespace NG.GIS.CAD.Exporter.Services;

public sealed class ArcGisTokenCacheRecord
{
    public string PortalSharingRoot { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string Username { get; set; } = string.Empty;
}
