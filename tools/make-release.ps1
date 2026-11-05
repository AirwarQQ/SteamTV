# Builds the two release zips into dist\:
#   NekoCouch-vX.Y.Z-win.zip — NekoCouch.exe, what the app's own updater downloads;
#   SteamTV-vX.Y.Z-win.zip   — the same app as SteamTV.exe with the old product name, because the
#                              updater of SteamTV 2.0 and older only takes that. On its first start
#                              it renames itself to NekoCouch.exe and carries everything over.
# Upload both to the GitHub release.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$proj = Get-Content "$root\NekoCouch.csproj"
$tag = 'v' + ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$dist = "$root\dist"; $tmp = "$dist\tmp"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$tmp\new", "$tmp\legacy" | Out-Null

dotnet build "$root\NekoCouch.csproj" -c Release -o "$tmp\new\out" | Out-Null
if ($LASTEXITCODE) { throw "build failed" }
dotnet build "$root\NekoCouch.csproj" -c Release -p:Product=SteamTV -o "$tmp\legacy\out" | Out-Null
if ($LASTEXITCODE) { throw "legacy build failed" }
# Leave obj\ as a normal build again.
dotnet build "$root\NekoCouch.csproj" -c Release | Out-Null

Copy-Item "$tmp\new\out\NekoCouch.exe" "$tmp\new\NekoCouch.exe"
Copy-Item "$tmp\legacy\out\NekoCouch.exe" "$tmp\legacy\SteamTV.exe"
foreach ($c in @(@("$tmp\new\NekoCouch.exe", 'NekoCouch'), @("$tmp\legacy\SteamTV.exe", 'SteamTV'))) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($c[0])
    if ($info.ProductName -ne $c[1]) { throw "$($c[0]) has product name '$($info.ProductName)', expected '$($c[1])'" }
}
Compress-Archive "$tmp\new\NekoCouch.exe" "$dist\NekoCouch-$tag-win.zip" -Force
Compress-Archive "$tmp\legacy\SteamTV.exe" "$dist\SteamTV-$tag-win.zip" -Force
Remove-Item $tmp -Recurse -Force
Get-ChildItem $dist -Filter "*$tag-win.zip" | ForEach-Object { "{0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB) }
