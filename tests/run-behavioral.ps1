# Behavioural tests on Windows, with the C# compiler that ships with .NET Framework 4.x (no Visual Studio needed).
# NOTE: written for Windows but not yet run on it; tests/run-behavioral.sh (Mono) is the version the maintainer runs.
#   powershell -ExecutionPolicy Bypass -File tests\run-behavioral.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Write-Error "csc.exe not found (it ships with .NET Framework 4.x)."; exit 2 }

$out = Join-Path $env:TEMP "dtn-behavioral"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path "$out\src" | Out-Null

# The stand-in has no WPF media or DataAnnotations; the add-on does not use them.
Get-ChildItem "$root\src\DirectTelegramNotifier\*.cs" | ForEach-Object {
    Get-Content $_.FullName | Where-Object { $_ -notmatch '^using (System\.Windows\.Media(\.Imaging)?|System\.ComponentModel\.DataAnnotations);' } |
        Set-Content -Path (Join-Path "$out\src" $_.Name) -Encoding UTF8
}

& $csc /nologo /target:library /out:"$out\AddOn.dll" /nowarn:0168,0219,0414,0649,0169,1998,0618 `
    /r:System.Net.Http.dll /r:System.Core.dll /r:System.Xml.dll "$root\tests\behavioral\NinjaTraderStandIn.cs" "$out\src\*.cs"
if ($LASTEXITCODE -ne 0) { Write-Error "The add-on did not compile."; exit 2 }
Write-Host "The add-on compiles."

$status = 0
Get-ChildItem "$root\tests\behavioral\[0-9]*.cs" | ForEach-Object {
    $name = $_.BaseName
    & $csc /nologo /nowarn:0168,0219,0618 /out:"$out\$name.exe" /r:"$out\AddOn.dll" /r:System.Net.Http.dll $_.FullName
    if ($LASTEXITCODE -ne 0) { Write-Host "FAIL  $name (did not compile)"; $script:status = 1; return }
    Push-Location $out
    & "$out\$name.exe" *> "$out\$name.log"
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -eq 0) { Write-Host "ok    $name" } else { Write-Host "FAIL  $name (see $out\$name.log)"; $script:status = 1 }
}
if ($status -eq 0) { Write-Host "All behavioural suites passed." } else { Write-Host "Some behavioural suites FAILED." }
exit $status
