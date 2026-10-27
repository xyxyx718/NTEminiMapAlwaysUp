$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Start-App.ps1')
$cases = @(
    @{ Name = 'Both .NET 9 frameworks'; Lines = @('Microsoft.NETCore.App 9.0.21 [C:\dotnet\shared\Microsoft.NETCore.App]', 'Microsoft.WindowsDesktop.App 9.0.21 [C:\dotnet\shared\Microsoft.WindowsDesktop.App]'); Expected = $true },
    @{ Name = 'Core runtime alone is insufficient'; Lines = @('Microsoft.NETCore.App 9.0.21 [C:\dotnet\shared]'); Expected = $false },
    @{ Name = 'Desktop runtime alone is incomplete'; Lines = @('Microsoft.WindowsDesktop.App 9.0.21 [C:\dotnet\shared]'); Expected = $false },
    @{ Name = 'Other major versions are insufficient'; Lines = @('Microsoft.NETCore.App 8.0.0 [C:\dotnet\shared]', 'Microsoft.WindowsDesktop.App 10.0.0 [C:\dotnet\shared]'); Expected = $false },
    @{ Name = 'Missing runtimes'; Lines = @(); Expected = $false },
    @{ Name = 'Preview runtime is not accepted as stable'; Lines = @('Microsoft.NETCore.App 9.0.0-preview.1 [C:\dotnet\shared]', 'Microsoft.WindowsDesktop.App 9.0.0-preview.1 [C:\dotnet\shared]'); Expected = $false }
)
foreach ($case in $cases) {
    if ((Test-RuntimeLines $case.Lines) -ne $case.Expected) { throw ('Failed: ' + $case.Name) }
}
Write-Output ('Passed ' + $cases.Count + ' dependency probe cases; no installation performed.')
