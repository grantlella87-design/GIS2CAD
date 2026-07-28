namespace NG.GIS.CAD.Exporter.Services;
public sealed class AppServices
{
    public required ArcGisRestClient ArcGisRestClient { get; init; }
    public required ArcGisTokenProvider ArcGisTokenProvider { get; init; }
    public required ExportProfileStore ProfileStore { get; init; }
    public required UserSettingsStore UserSettingsStore { get; init; }
    public required ExportCoordinator ExportCoordinator { get; init; }
    public required CadDrawingCatalog CadDrawingCatalog { get; init; }
    public required ExportPlanStore ExportPlanStore { get; init; }
    public required CadExtentService CadExtentService { get; init; }
    public static AppServices CreateDefault()
    {
        var httpClient = new HttpClient();
        var tokenProvider = new ArcGisTokenProvider(httpClient);
        var settingsStore = new UserSettingsStore();
        var arcGisRestClient = new ArcGisRestClient(httpClient, tokenProvider);
        return new AppServices
        {
            ArcGisRestClient = arcGisRestClient,
            ArcGisTokenProvider = tokenProvider,
            ProfileStore = new ExportProfileStore(),
            UserSettingsStore = settingsStore,
            ExportCoordinator = new ExportCoordinator(arcGisRestClient, settingsStore),
            CadDrawingCatalog = new CadDrawingCatalog(),
            ExportPlanStore = new ExportPlanStore(),
            CadExtentService = new CadExtentService()
        };
    }
}

