# Builds discord-nowcast.exe with the C# compiler that ships with .NET
# Framework 4.8 on every Windows 10 and 11: nothing to install.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1

param([string]$OutputName = 'discord-nowcast.exe')

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$csc = Join-Path $framework 'csc.exe'

$references = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Net.Http.dll', 'System.Web.Extensions.dll',
    'System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll'
) | ForEach-Object { '-r:' + (Join-Path $framework $_) }
$references += @('UIAutomationClient.dll', 'UIAutomationTypes.dll') | ForEach-Object { '-r:' + (Join-Path $wpf $_) }

$out = Join-Path $here $OutputName
& $csc -nologo -target:winexe -optimize+ -codepage:65001 `
    "-out:$out" `
    "-win32icon:$(Join-Path $here 'assets\icon.ico')" `
    "-win32manifest:$(Join-Path $here 'src\app.manifest')" `
    @references (Join-Path $here 'src\*.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Built $out"
