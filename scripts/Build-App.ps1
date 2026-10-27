[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts\dotnet-cli'
if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
& dotnet publish (Join-Path $projectRoot 'MinimapUp.csproj') -c Release -o (Join-Path $projectRoot 'app') -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '构建失败，请检查输出；若程序仍在运行，请先从托盘退出。' }
if (!$SkipTests) {
    & dotnet (Join-Path $projectRoot 'app\MinimapUp.dll') --test-behavior
    if ($LASTEXITCODE -ne 0) { throw '行为检查失败。' }
    $reports = Join-Path $projectRoot 'artifacts\reports'
    New-Item -ItemType Directory -Path $reports -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'app\behavior-test-results.json') -Destination $reports
}
