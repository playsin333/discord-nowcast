# Builds the checks in tests\ together with the program and runs them, with
# the same compiler as build.ps1. Exit code 1 when a check fails.
#
#   powershell -ExecutionPolicy Bypass -File tests\run.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'

$references = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Net.Http.dll', 'System.Web.Extensions.dll',
    'System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll'
) | ForEach-Object { '-r:' + (Join-Path $framework $_) }

$out = Join-Path ([IO.Path]::GetTempPath()) 'discord-nowcast-tests.exe'
& $csc -nologo -target:exe -codepage:65001 -main:Nowcast.Tests "-out:$out" `
    @references (Join-Path $root 'src\*.cs') (Join-Path $root 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
& $out
exit $LASTEXITCODE
