[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$NoGui,
    [switch]$InstallDotNet,
    [switch]$InstallOcr
)

# Windows PowerShell 5.1 uses the OS .NET Framework and can run even when .NET 9 is absent.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $projectRoot 'app'
$appPath = Join-Path $appDirectory 'MinimapUp.exe'
$logPath = Join-Path $appDirectory 'dependency-install.log'

function Test-RuntimeLines([string[]]$Lines) {
    $desktop = @($Lines | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App\s+9\.0\.\d+\s+\[' }).Count -gt 0
    $core = @($Lines | Where-Object { $_ -match '^Microsoft\.NETCore\.App\s+9\.0\.\d+\s+\[' }).Count -gt 0
    return $desktop -and $core
}

function Find-WinGet {
    $command = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    try {
        $package = Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction Stop | Select-Object -First 1
        if ($package) {
            $candidate = Join-Path $package.InstallLocation 'winget.exe'
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    } catch { }
    return $null
}

function Get-Dependencies {
    $roots = @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)
    try { $roots += (Get-ItemProperty 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -ErrorAction Stop).InstallLocation } catch { }
    $roots += Join-Path $env:ProgramFiles 'dotnet'
    $runtime = $false
    $runtimePath = $null
    foreach ($root in @($roots | Where-Object { $_ } | Select-Object -Unique)) {
        $dotnet = Join-Path $root 'dotnet.exe'
        if (!(Test-Path -LiteralPath $dotnet)) { continue }
        try {
            $lines = @(& $dotnet --list-runtimes 2>$null)
            if ($LASTEXITCODE -eq 0 -and (Test-RuntimeLines $lines)) { $runtime = $true; $runtimePath = $dotnet; break }
        } catch { }
    }
    $ocr = $false
    $ocrError = $null
    try {
        [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
        [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
        $language = [Windows.Globalization.Language]::new('zh-Hans')
        $ocr = $null -ne [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
    } catch { $ocrError = $_.Exception.Message }
    $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    [pscustomobject]@{
        SupportedWindows = [Environment]::Is64BitOperatingSystem -and $build -ge 19041
        WindowsBuild = $build
        DesktopRuntime9 = $runtime
        DotNetPath = $runtimePath
        ChineseOcr = $ocr
        OcrError = $ocrError
        WinGetAvailable = [bool](Find-WinGet)
    }
}

function Show-Message([string]$Text, [string]$Buttons = 'OK', [string]$Icon = 'Information') {
    [Windows.Forms.MessageBox]::Show($Text, '异环小地图朝上 · 依赖检查', $Buttons, $Icon)
}

function Invoke-DependencyInstall([string]$Component) {
    $form = [Windows.Forms.Form]::new()
    $form.Text = '安装运行依赖'
    $form.ClientSize = [Drawing.Size]::new(440, 110)
    $form.StartPosition = 'CenterScreen'; $form.ControlBox = $false
    $label = [Windows.Forms.Label]::new()
    $label.Text = "正在安装 $Component，请批准系统权限提示。`r`n可能需要几分钟，完成后会重新检查。"
    $label.Dock = 'Top'; $label.Height = 65; $label.Padding = [Windows.Forms.Padding]::new(12)
    $bar = [Windows.Forms.ProgressBar]::new(); $bar.Dock = 'Bottom'; $bar.Style = 'Marquee'
    $form.Controls.Add($label); $form.Controls.Add($bar); $form.Show()
    try {
        $flag = if ($Component -eq '.NET Desktop Runtime 9') { '-InstallDotNet' } else { '-InstallOcr' }
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" ' + $flag
        $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
        while (!$process.HasExited) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 150; $process.Refresh() }
        $process.WaitForExit()
        return $process.ExitCode -eq 0
    }
    catch { $_.Exception.Message | Out-File -LiteralPath $logPath -Append -Encoding utf8; return $false }
    finally { $form.Close(); $form.Dispose() }
}

# Dot-sourcing exposes read-only probes for the launcher tests without running the UI.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    if ($InstallDotNet -or $InstallOcr) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '安装依赖需要管理员权限。' }
        if ($InstallDotNet) {
            $winget = Find-WinGet
            if (!$winget) { throw '未找到 WinGet。请从 https://dotnet.microsoft.com/download/dotnet/9.0 安装 x64 Desktop Runtime。' }
            $stdout = Join-Path $appDirectory 'winget-install.stdout.log'
            $stderr = Join-Path $appDirectory 'winget-install.stderr.log'
            $process = Start-Process -FilePath $winget -ArgumentList @('install', '--id', 'Microsoft.DotNet.DesktopRuntime.9', '--exact', '--source', 'winget', '--architecture', 'x64', '--silent', '--disable-interactivity', '--accept-source-agreements', '--accept-package-agreements') -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            Get-Content -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue | Out-File -LiteralPath $logPath -Append -Encoding utf8
            if ($process.ExitCode -notin @(0, 3010)) { throw "WinGet 安装失败，退出码 $($process.ExitCode)。" }
        }
        if ($InstallOcr) {
            Import-Module Dism
            $result = Add-WindowsCapability -Online -Name 'Language.OCR~~~zh-CN~0.0.1.0' -ErrorAction Stop
            $result | Out-File -LiteralPath $logPath -Append -Encoding utf8
        }
        exit 0
    }

    $state = Get-Dependencies
    if ($CheckOnly -and $NoGui) { $state | ConvertTo-Json; if ($state.SupportedWindows -and $state.DesktopRuntime9 -and $state.ChineseOcr) { exit 0 } else { exit 2 } }
    Add-Type -AssemblyName System.Windows.Forms
    if ($CheckOnly) {
        Show-Message ("Windows 支持：{0}`r`n.NET Desktop Runtime 9：{1}`r`n简体中文 OCR：{2}`r`nWinGet 可用：{3}" -f $state.SupportedWindows, $state.DesktopRuntime9, $state.ChineseOcr, $state.WinGetAvailable) | Out-Null
        exit 0
    }
    if (!$state.SupportedWindows) { throw '本版本需要 64 位 Windows 10 2004（内部版本 19041）或更新系统。' }
    if (!(Test-Path -LiteralPath $appPath)) { throw "未找到程序：$appPath`r`n请先构建或解压完整目录。" }
    if (!$state.DesktopRuntime9) {
        if (!$state.WinGetAvailable) { throw '缺少 .NET Desktop Runtime 9，且 WinGet 不可用。请从 https://dotnet.microsoft.com/download/dotnet/9.0 安装 x64 Desktop Runtime 后重试。' }
        $answer = Show-Message "缺少 .NET Desktop Runtime 9（x64）。`r`n是否通过 WinGet 自动安装？安装需要联网和管理员权限。" 'YesNo' 'Question'
        if ($answer -ne 'Yes') { exit 2 }
        $installed = Invoke-DependencyInstall '.NET Desktop Runtime 9'
        $state = Get-Dependencies
        if (!$installed -or !$state.DesktopRuntime9) { throw "运行库安装未完成。请查看 $logPath；若安装器要求重启，请重启后重试。" }
    }
    $needsOcr = $true
    $settingsPath = Join-Path $appDirectory 'settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if ($settings.DrivingOnly -eq $false) { $needsOcr = $false }
        } catch { }
    }
    if (!$state.ChineseOcr -and $needsOcr) {
        $answer = Show-Message "缺少简体中文 OCR，驾驶模式文字识别将不可用。`r`n是否安装 Windows 中文 OCR 按需组件？需要联网和管理员权限。`r`n选择“否”仍可启动程序，并在界面取消驾驶模式限制。" 'YesNo' 'Question'
        if ($answer -eq 'Yes') {
            $installed = Invoke-DependencyInstall '简体中文 OCR'
            $state = Get-Dependencies
            if (!$installed -or !$state.ChineseOcr) {
                Show-Message "中文 OCR 仍不可用。请查看 $logPath，或按安装结果要求重启。`r`n程序会继续启动，可先取消驾驶模式限制。" 'OK' 'Warning' | Out-Null
            }
        }
    }
    Start-Process -FilePath $appPath -WorkingDirectory $appDirectory
}
catch {
    if (Test-Path -LiteralPath $appDirectory) { $_.Exception.ToString() | Out-File -LiteralPath $logPath -Append -Encoding utf8 }
    if ($NoGui -or $InstallDotNet -or $InstallOcr) { Write-Error $_.Exception.Message -ErrorAction Continue }
    else { Add-Type -AssemblyName System.Windows.Forms; Show-Message $_.Exception.Message 'OK' 'Error' | Out-Null }
    exit 1
}
