# NG GIS CAD Exporter Starter

This is a starter AutoCAD .NET/WPF plug-in shell for exporting National Grid ArcGIS REST layers to CAD.

## What is included

- AutoCAD command entry point: `NGGIS`
- WPF user interface for:
  - loading ArcGIS layer metadata
  - toggling layers on/off
  - selecting exported fields per layer
  - saving and loading previous user settings
- ArcGIS REST metadata client
- user settings JSON persistence under `%LOCALAPPDATA%\NationalGrid\GisCadExporter\user-settings.json`
- profile JSON for National Grid services
- export pipeline placeholders for the next phase

## Current scope

This first cut intentionally focuses on the application shell and user choices. It does not yet write DWG entities or stage features for Export To CAD. The intended next step is to add the REST feature download/staging pipeline behind `ExportCoordinator`.

## Build notes

AutoCAD managed assemblies are not included. Set `AcadReferencePath` in `Directory.Build.props` to your local AutoCAD or Civil 3D install folder that contains:

- AcMgd.dll
- AcCoreMgd.dll
- AcDbMgd.dll

Then build with Visual Studio.

## Load in AutoCAD

1. Build the project.
2. In AutoCAD, run `NETLOAD`.
3. Load `NG.GIS.CAD.Exporter.dll`.
4. Run command `NGGIS`.

## Files to edit first

- `profiles/ng-gis-export-profile.json`
- `src/NG.GIS.CAD.Exporter/Services/ExportCoordinator.cs`
- `src/NG.GIS.CAD.Exporter/Services/ArcGisRestClient.cs`
