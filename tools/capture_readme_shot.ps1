# Regenerates docs/media/10000-agents.png from the shipped player.
#
# The screenshot is produced by the build, not captured by hand, so it can be regenerated whenever
# the numbers move and the figures in it always come from a run anyone can repeat. The player draws
# it itself: keyboard automation is unreliable here, because Unity frequently ignores synthesized
# input, and a screenshot that silently captured the wrong technique state would be worse than none.
#
#   pwsh -File tools/capture_readme_shot.ps1
#
# Check the figures it prints against the results table in README.md before committing the image.

[CmdletBinding()]
param(
    [int]    $Agents       = 10000,
    [string] $Techniques   = 'spatialHash,zeroAlloc,gpuInstancing',
    # 120 frames of rolling window plus warm-up. The player raises anything lower, because a median
    # over a window that is still filling is not the statistic the README quotes.
    [int]    $SettleFrames = 240,
    [int]    $Width        = 1280,
    [int]    $Height       = 720,
    [string] $Output       = 'docs/media/10000-agents.png'
)

$ErrorActionPreference = 'Stop'

$repo   = Split-Path -Parent $PSScriptRoot
$player = Join-Path $repo 'Builds/Windows64/FrameBudget.exe'
$png    = Join-Path $repo $Output
$log    = Join-Path ([System.IO.Path]::GetTempPath()) 'framebudget-capture.log'

if (-not (Test-Path $player)) {
    Write-Error @"
No player at $player

Build one first:
  & "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe" -batchmode -quit ``
      -projectPath "$repo" -executeMethod FrameBudget.EditorTools.BuildBenchmark.Build

The screenshot has to come from a release player: the HUD prints (Player) or (Editor) in its first
line and a reader will check.
"@
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $png) | Out-Null
if (Test-Path $png) { Remove-Item $png -Force }

Write-Host "Capturing $Agents agents [$Techniques] at ${Width}x${Height}, settling $SettleFrames frames..."

& $player `
    -screen-fullscreen 0 -screen-width $Width -screen-height $Height `
    -frameBudgetCapture $png `
    -frameBudgetAgents $Agents `
    -frameBudgetTechniques $Techniques `
    -frameBudgetSettleFrames $SettleFrames `
    -logFile $log | Out-Null

if (-not (Test-Path $png)) {
    Write-Host ''
    Write-Host 'The player exited without writing the image. Its log:' -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 40 }
    Write-Error "No image at $png"
    exit 1
}

$image = Get-Item $png
Write-Host ''
Write-Host "Wrote $($image.FullName) ($([math]::Round($image.Length / 1KB)) KB)" -ForegroundColor Green

# The figures the capture logged, so they can be checked against the README table.
$figures = Select-String -Path $log -Pattern '\[FrameBudget\] Capture figures: (.*)$' |
           Select-Object -Last 1
if ($figures) {
    Write-Host ''
    Write-Host 'Panel figures at capture time - check these against the table in README.md:'
    foreach ($pair in ($figures.Matches[0].Groups[1].Value -split ' ')) {
        Write-Host "  $pair"
    }
    Write-Host ''
    Write-Host 'Frame time is a single cold run and sits a little under the table median, which is'
    Write-Host 'taken from five interleaved runs on a warmer machine. Draw calls and the technique'
    Write-Host 'state should match exactly.'
} else {
    Write-Warning "The capture logged no figures line; check $log"
}
