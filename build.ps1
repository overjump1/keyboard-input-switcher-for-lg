# Builds lg-input-switcher with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
#   bin\lg-input-switcher.exe      tray app (no console window)
#   bin\lg-input-switcher-cli.exe  same code, console build for scripting/testing
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = $PSScriptRoot
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$common = @('/nologo', '/platform:x64', '/optimize+', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')

foreach ($build in @(@{ Target = 'winexe'; Name = 'lg-input-switcher.exe'; Define = 'TRAY' }, @{ Target = 'exe'; Name = 'lg-input-switcher-cli.exe'; Define = 'CLI' })) {
    & $csc @common "/target:$($build.Target)" "/define:$($build.Define)" "/out:$(Join-Path $out $build.Name)" @sources
    if ($LASTEXITCODE -ne 0) { throw "Build of $($build.Name) failed" }
}

Write-Host "Built: $out\lg-input-switcher.exe, $out\lg-input-switcher-cli.exe"
