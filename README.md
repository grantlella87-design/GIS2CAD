# NG GIS CAD Exporter

An AutoCAD .NET / WPF plug-in for pulling National Grid ArcGIS content into CAD drawings.

## What is included

- AutoCAD command entry point: `NGGIS`
- ArcGIS portal OAuth sign-in with a cached refresh token
- ArcGIS REST metadata/feature client and an ArcGIS Runtime map view
- WPF wizard for:
  - browsing NG_ODS work orders and matching them to drawing extents
  - loading ArcGIS layer metadata, toggling layers, and selecting exported fields
  - drawing and editing proposed main pipeline segments (single and multi-segment)
  - browsing local DWT templates
- Profile JSON for National Grid services (`profiles/ng-gis-export-profile.json`)
- User settings persisted to `%LOCALAPPDATA%\NationalGrid\GisCadExporter\user-settings.json`

## Current scope

The wizard, portal authentication, work order lookup, and proposed-main editing paths are
implemented. The generic layer export pipeline is still a placeholder:
`ExportCoordinator.RunExportAsync` validates the selection but does not yet stage features or
write DWG entities. That is the intended next step.

## Configuration

### AutoCAD assemblies

AutoCAD managed assemblies are not included in the repository. Set `AcadReferencePath` in
`Directory.Build.props` to your local AutoCAD or Civil 3D install folder, which must contain:

- `AcMgd.dll`
- `AcCoreMgd.dll`
- `AcDbMgd.dll`

### NG_ODS connection string

The work order lookup reads its SQL connection string from the `NGGISCAD_ODS_CONN` environment
variable. Nothing is committed to the repository, and the lookup throws a descriptive error if the
variable is unset. Set it once per machine, for example:

```powershell
[Environment]::SetEnvironmentVariable('NGGISCAD_ODS_CONN', '<connection string>', 'User')
```

Never commit a connection string, token, or password to this repository.

## Build

```powershell
.\tools\build.ps1
```

Or build `NG.GIS.CAD.Exporter.sln` in Visual Studio. The project targets
`net8.0-windows10.0.19041.0` with WPF, so it builds on Windows only.

## Load in AutoCAD

1. Build the project.
2. In AutoCAD, run `NETLOAD`.
3. Load `NG.GIS.CAD.Exporter.dll`.
4. Run command `NGGIS`.

## Repository conventions

Do not commit `.bak`, `.disabled`, or timestamped backup copies of source files — git history is
the backup. These patterns are covered by `.gitignore`.
