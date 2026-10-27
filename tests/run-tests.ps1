# Runs the core unit tests with the C# compiler that ships with Windows (.NET Framework 4.x).
# No Visual Studio, NuGet or NinjaTrader needed:   powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Write-Error "csc.exe not found. It ships with .NET Framework 4.x, which NinjaTrader 8 requires."; exit 2 }

$core  = Join-Path $root "src\DirectTelegramNotifier\DirectTelegramNotifier.Core.cs"
$tests = Join-Path $PSScriptRoot "CoreTests.cs"
$out   = Join-Path $env:TEMP "DirectTelegramNotifier.CoreTests.exe"

& $csc /nologo /optimize /out:$out /r:System.Core.dll $core $tests
if ($LASTEXITCODE -ne 0) { Write-Error "Compile failed."; exit $LASTEXITCODE }
& $out
exit $LASTEXITCODE
