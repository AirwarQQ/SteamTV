# Builds dist\NekoCouch-vX.Y.Z-win.zip (NekoCouch.exe only), the file the app's updater downloads.
# Upload it to the GitHub release for tag vX.Y.Z.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$proj = Get-Content "$root\NekoCouch.csproj"
$tag = 'v' + ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$out = "$root\bin\Release\net48"; $dist = "$root\dist"

dotnet build "$root\NekoCouch.csproj" -c Release | Out-Null
if ($LASTEXITCODE) { throw "build failed" }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo("$out\NekoCouch.exe").ProductName -ne 'NekoCouch') { throw "unexpected product name" }

New-Item -ItemType Directory -Force $dist | Out-Null
Compress-Archive "$out\NekoCouch.exe" "$dist\NekoCouch-$tag-win.zip" -Force
Get-Item "$dist\NekoCouch-$tag-win.zip" | ForEach-Object { "{0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB) }
