$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Push-Location $Root
try {
    dotnet build .\NG.GIS.CAD.Exporter.sln -c Debug
}
finally {
    Pop-Location
}
