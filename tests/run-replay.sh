#!/usr/bin/env bash
# Replays recorded risk logs through the add-on's risk logic (the pure core, no NinjaTrader needed). Needs Mono.
#   bash tests/run-replay.sh                       run every case in tests/replay/cases (each: risk.csv + expected.txt)
#   bash tests/run-replay.sh path/to/log.csv [--account NAME]     print the timeline for a log you recorded
# To turn something you saw on Sim into a permanent test: see "Replay tests" in docs/TESTING.md.
set -u
cd "$(dirname "$0")/.."
OUT="${TMPDIR:-/tmp}/dtn-replay"; rm -rf "$OUT"; mkdir -p "$OUT"
mcs -langversion:5 -out:"$OUT/Replay.exe" -r:System.Core.dll src/DirectTelegramNotifier/DirectTelegramNotifier.Core.cs tests/replay/Replay.cs || { echo "did not compile"; exit 2; }

if [ $# -gt 0 ]; then mono "$OUT/Replay.exe" "$@"; exit $?; fi

status=0
for dir in tests/replay/cases/*/; do
  name="$(basename "$dir")"
  if mono "$OUT/Replay.exe" "$dir/risk.csv" --expect "$dir/expected.txt" > "$OUT/$name.log" 2>&1; then
    echo "ok    $name"
  else
    echo "FAIL  $name"; head -12 "$OUT/$name.log"; status=1
  fi
done
[ $status -eq 0 ] && echo "All replay cases passed." || echo "Some replay cases FAILED."
exit $status
