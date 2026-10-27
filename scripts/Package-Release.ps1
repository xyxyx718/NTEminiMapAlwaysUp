[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$')][string]$Version = '0.1.0',
    [switch]$SkipTests,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $projectRoot 'artifacts'
$work = Join-Path $artifacts ('release-builds\' + $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$releases = Join-Path $artifacts ('releases\v' + $Version)
$env:DOTNET_CLI_HOME = Join-Path $artifacts 'dotnet-cli'
if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
New-Item -ItemType Directory -Path $work, $releases -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$results = @()

function Invoke-AppTest([string]$Executable, [string[]]$AppArguments) {
    $arguments = ($AppArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Test timed out: $Executable $arguments" }
    if ($process.ExitCode -ne 0) { throw "Test failed: $Executable $arguments (exit $($process.ExitCode)); see error.log beside the executable." }
}

foreach ($variant in @('framework-dependent', 'self-contained')) {
    $selfContained = $variant -eq 'self-contained'
    $publish = Join-Path $work ($variant + '-publish')
    $stage = Join-Path $work $variant
    $containedArgument = if ($selfContained) { 'true' } else { 'false' }
    & dotnet publish (Join-Path $projectRoot 'MinimapUp.csproj') -c Release -r win-x64 --self-contained $containedArgument -o $publish -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$containedArgument -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $variant" }
    $executable = Join-Path $publish 'MinimapUp.exe'
    if (!$SkipTests) {
        Invoke-AppTest $executable @('--test-behavior')
        $report = Get-Content -LiteralPath (Join-Path $publish 'behavior-test-results.json') -Raw | ConvertFrom-Json
        if ($report.failures -ne 0) { throw "Behavior regression failed: $variant" }
        $diagnostics = Join-Path $work ($variant + '-tests')
        New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
        foreach ($sample in @('driving-keyboard.png', 'live-game.png')) {
            $fixturePath = Join-Path $diagnostics $sample
            Copy-Item -LiteralPath (Join-Path $projectRoot ('tests\fixtures\' + $sample)) -Destination $fixturePath
            Invoke-AppTest $executable @('--test-driving', $fixturePath)
            $detected = Get-Content -LiteralPath ($fixturePath + '.driving.json') -Raw | ConvertFrom-Json
            $expected = $sample -eq 'driving-keyboard.png'
            if ($detected.Error -or $detected.Detected -ne $expected) { throw "OCR regression failed: $variant / $sample" }
        }
    }
    New-Item -ItemType Directory -Path $stage, (Join-Path $stage 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath $executable, (Join-Path $publish 'arrow-template.png') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot '启动小地图.cmd'), (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\Start-App.ps1') -Destination (Join-Path $stage 'scripts')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSES') -Destination $stage -Recurse
    [ordered]@{ version = $Version; runtimeIdentifier = 'win-x64'; selfContained = $selfContained; requiresChineseOcr = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'release.json') -Encoding UTF8
    if ($selfContained) {
        $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts\obj\project.assets.json') -Raw | ConvertFrom-Json
        $runtimePackages = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$' })
        if ($runtimePackages.Count -ne 2) { throw 'Could not resolve the two bundled runtime packages for license collection.' }
        $noticeCount = 0
        foreach ($dependency in $runtimePackages) {
            $packageVersion = ($dependency.version.Trim([char[]]'[]').Split(','))[0].Trim()
            $package = Join-Path $env:NUGET_PACKAGES ($dependency.name.ToLowerInvariant() + '\' + $packageVersion)
            $label = $dependency.name + '-' + $packageVersion
            foreach ($notice in Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' }) {
                Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $stage ('LICENSES\' + $label + '-' + $notice.Name))
                $noticeCount++
            }
        }
        if ($noticeCount -lt 3) { throw 'Bundled runtime license/third-party notices are incomplete.' }
    }
    # Only staged publish files enter the archive: no user configuration, logs,
    # PDBs, source, screenshots or local diagnostics are copied into the package.
    $archiveName = if ($selfContained) { 'NTEminiMapAlwaysUp-WithDotNet.zip' } else { 'NTEminiMapAlwaysUp.zip' }
    $zip = Join-Path $releases $archiveName
    if (Test-Path -LiteralPath $zip) {
        if (!$Force) { throw "Archive already exists: $zip. Use -Force to replace generated archives for this version." }
        Remove-Item -LiteralPath $zip
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $results += [pscustomobject]@{ Variant = $variant; Zip = $zip; Bytes = (Get-Item -LiteralPath $zip).Length; UnpackedBytes = (Get-ChildItem -LiteralPath $stage -File -Recurse | Measure-Object Length -Sum).Sum }
}
$checksums = foreach ($result in $results) { $hash = Get-FileHash -LiteralPath $result.Zip -Algorithm SHA256; $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($result.Zip) }
$checksums | Set-Content -LiteralPath (Join-Path $releases 'SHA256SUMS.txt') -Encoding ASCII
$results | Format-Table Variant, Bytes, UnpackedBytes, Zip -AutoSize
