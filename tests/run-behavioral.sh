#!/usr/bin/env bash
# Behavioural tests: compile the whole add-on against a small stand-in for the NinjaTrader API
# (tests/behavioral/NinjaTraderStandIn.cs) and drive it through the same entry points NinjaTrader and Telegram use.
# Needs Mono (mcs + mono).  Usage:  bash tests/run-behavioral.sh
# These prove the logic and wiring. They cannot prove how real NinjaTrader or your broker connection behaves:
# for that, use the Sim checklist in docs/TESTING.md.
set -u
cd "$(dirname "$0")/.."
OUT="${TMPDIR:-/tmp}/dtn-behavioral"
rm -rf "$OUT"; mkdir -p "$OUT/src"

# The stand-in has no WPF media or DataAnnotations; the add-on does not use them.
for f in src/DirectTelegramNotifier/*.cs; do
  grep -v -E '^using (System\.Windows\.Media(\.Imaging)?|System\.ComponentModel\.DataAnnotations);' "$f" > "$OUT/src/$(basename "$f")"
done

mcs -langversion:5 -target:library -out:"$OUT/AddOn.dll" -nowarn:0168,0219,0414,0649,0169,1998,0618 \
  -r:System.Net.Http.dll -r:System.Core.dll -r:System.Xml.dll \
  tests/behavioral/NinjaTraderStandIn.cs "$OUT"/src/*.cs || { echo "The add-on did not compile."; exit 2; }
echo "The add-on compiles at C# 5."

status=0
for h in tests/behavioral/[0-9]*.cs; do
  name="$(basename "$h" .cs)"
  mcs -langversion:5 -nowarn:0168,0219,0618 -out:"$OUT/$name.exe" -r:"$OUT/AddOn.dll" -r:System.Net.Http.dll "$h" \
    || { echo "FAIL  $name (did not compile)"; status=1; continue; }
  if mono "$OUT/$name.exe" > "$OUT/$name.log" 2>&1; then
    echo "ok    $name: $(grep -E 'passed|PASSED|warnings: 0' "$OUT/$name.log" | tail -1)"
  else
    echo "FAIL  $name"; grep -E '^FAIL|FAILED|Exception' "$OUT/$name.log" | head -10; status=1
  fi
done
[ $status -eq 0 ] && echo "All behavioural suites passed." || echo "Some behavioural suites FAILED (logs in $OUT)."
exit $status
