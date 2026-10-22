# Builds everything and runs the tests. No real input is generated, so the default run is safe
# while you keep working.
#   -Place  also opens two fake game windows in the background and moves them through every window
#           layout. They open in the background, but maximizing one can bring it to the front, and
#           they appear on your monitors for a few seconds. Fine while not playing.
#   -Focus  also swaps focus between two fake game windows for real, which steals focus for about
#           half a second. Don't run it while playing.
param([switch]$Focus, [switch]$Place)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build "$root\WowDualBoxAssist.slnx" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$fake = "$root\tests\FakeGame\bin\Release\net10.0-windows\FakeGame.exe"
$tests = "$root\tests\DualBoxAssistTests\bin\Release\net10.0-windows\DualBoxAssistTests.exe"

if (-not $Focus -and -not $Place) {
    & $tests
    exit $LASTEXITCODE
}

$logA = Join-Path $env:TEMP 'wowdualboxassist-test-a.log'
$logB = Join-Path $env:TEMP 'wowdualboxassist-test-b.log'
$games = @(
    (Start-Process $fake -ArgumentList @('"Fake Game A"', $logA, 100) -PassThru),
    (Start-Process $fake -ArgumentList @('"Fake Game B"', $logB, 560) -PassThru)
)
try {
    Start-Sleep -Milliseconds 1200
    & $tests $(if ($Focus) { '--focus' } else { '--place' }) $logA $logB
    $code = $LASTEXITCODE
}
finally {
    foreach ($g in $games) {
        $g.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 300
        if (-not $g.HasExited) { $g.Kill() }
    }
}
exit $code
