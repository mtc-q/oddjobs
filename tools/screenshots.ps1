# Renders website screenshots of every app (light and dark) using the "Shots" build,
# which includes a hook that saves the window to a PNG. Run from the repo root:
#   powershell -ExecutionPolicy Bypass -File tools\screenshots.ps1 [-Apps Relabel,Twins] [-Out docs\assets\shots]
param(
    [string[]]$Apps = @("Relabel", "Twins", "Footprint", "Scrub", "Tidy", "Untangle", "Verity", "Binder", "Lockbox", "Parity"),
    [string]$Out = "docs\assets\shots",
    [string]$Demo = "C:\Users\Public\Odd Jobs demo",   # a path with no personal user name in it
    [string]$Size = "1200x760"
)
$ErrorActionPreference = "Stop"
$Apps = $Apps | ForEach-Object { $_ -split "," } | Where-Object { $_ }
$root = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
New-Item -ItemType Directory -Force $Out | Out-Null

dotnet build "$root\tools\DemoData" -c Release -nologo -v q | Out-Null
& "$root\tools\DemoData\bin\Release\net48\DemoData.exe" $Demo | Out-Null

# What each app is shown working on.
$argsFor = @{
    Relabel   = @("$Demo\Photos\2026 Lisbon trip")
    Twins     = @("$Demo\Photos", "$Demo\Downloads", "$Demo\Desktop")
    Footprint = @("$Demo")
    Scrub     = @("$Demo\Photos\2026 Lisbon trip")
    Tidy      = @("$Demo\Downloads")
    Untangle  = @("$Demo\copied-from-pdf.txt")
    Verity    = @("$Demo\Downloads\Invoice-2026-0412.pdf", "$Demo\Downloads\fonts-inter.zip")
    Binder    = @("$Demo\Scans")
    Lockbox   = @("$Demo\Downloads\Invoice-2026-0412.pdf", "$Demo\Downloads\budget 2026.xlsx")
    Parity    = @("$Demo\Parity\Documents", "$Demo\Parity\USB backup")
}

foreach ($app in $Apps) {
    $build = dotnet build "$root\src\$app" -c Shots -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-String "error" | Select-Object -First 5 | Write-Host; continue }
    $exe = "$root\src\$app\bin\Shots\net48\$app.exe"
    foreach ($theme in "light", "dark") {
        $env:ODDJOBS_SHOT = Join-Path $Out ("{0}-{1}.png" -f $app.ToLowerInvariant(), $theme)
        $env:ODDJOBS_SHOT_SIZE = $Size
        $env:ODDJOBS_THEME = $theme
        $env:ODDJOBS_DEMO = "1"
        $quoted = $argsFor[$app] | ForEach-Object { '"' + $_ + '"' }
        $p = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru
        if (-not $p.WaitForExit(60000)) { $p.Kill(); Write-Warning "$app ($theme) timed out" }
        Write-Host ("shot  {0,-10} {1}" -f $app, $theme)
    }
}
Remove-Item Env:ODDJOBS_SHOT, Env:ODDJOBS_SHOT_SIZE, Env:ODDJOBS_THEME, Env:ODDJOBS_DEMO -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $Demo -ErrorAction SilentlyContinue
