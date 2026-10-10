# Builds and runs the UI tests (tests\UiTests.cs) against the app sources.
# The tests use a fake monitor, so the real LG never switches. They briefly show the Settings
# window, press Ctrl+Alt+F13..F16, open Notepad, and toggle (then restore) "Start with Windows".
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = $PSScriptRoot
$out = Join-Path $root 'tests\out'
New-Item -ItemType Directory -Force $out | Out-Null

$sources = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs) + @(Get-ChildItem (Join-Path $root 'tests') -Filter *.cs) | ForEach-Object { $_.FullName }
$exe = Join-Path $out 'lg-input-switcher-tests.exe'
& $csc /nologo /platform:x64 /target:exe /main:LgInputSwitcher.Tests.UiTests "/out:$exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }

& $exe
exit $LASTEXITCODE
