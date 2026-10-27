# Replay tests on Windows, with the C# compiler that ships with .NET Framework 4.x.
# NOTE: written for Windows but not yet run on it; tests/run-replay.sh (Mono) is the version the maintainer runs.
#   powershell -ExecutionPolicy Bypass -File tests\replay\run-replay.ps1 [log.csv] [--account NAME]
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$exe = Join-Path $env:TEMP "dtn-replay.exe"
& $csc /nologo /out:$exe /r:System.Core.dll "$root\src\DirectTelegramNotifier\DirectTelegramNotifier.Core.cs" "$root\tests\replay\Replay.cs"
if ($LASTEXITCODE -ne 0) { Write-Error "did not compile"; exit 2 }
if ($args.Count -gt 0) { & $exe @args; exit $LASTEXITCODE }
$status = 0
Get-ChildItem "$root\tests\replay\cases" -Directory | ForEach-Object {
    & $exe "$($_.FullName)\risk.csv" --expect "$($_.FullName)\expected.txt" | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "ok    $($_.Name)" } else { Write-Host "FAIL  $($_.Name)"; $script:status = 1 }
}
exit $status
