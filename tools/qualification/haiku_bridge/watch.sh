#!/usr/bin/env bash
# Emits one line per new bridge request (bundled for a fresh agent) and one per run end. Args: <run-name>...
S="$(cd "$(dirname "$0")" && pwd -W)"
Q="C:/DocxHeaderExtractor-a99-closed-loop/artifacts/qualification"
declare -A seen
declare -A done_runs
while true; do
  for run in "$@"; do
    dir="$Q/$run"
    for req in "$dir"/luna-requests/*.request.json; do
      [ -e "$req" ] || continue
      sha=$(basename "$req" .request.json)
      [ -n "${seen[$sha]}" ] && continue
      [ -e "$dir/luna-responses/$sha.response.json" ] && { seen[$sha]=1; continue; }
      seen[$sha]=1
      b=$(python "$S/bundle.py" "$req" "$dir/luna-responses" "$S/bundles/$run" 2>&1 | tail -1)
      stage=$(python -c "import json,sys;print(json.load(open(sys.argv[1]+'/meta.json'))['stage'])" "$b" 2>/dev/null)
      echo "NEW run=$run stage=$stage bundle=$b"
    done
    log="$S/$run.log"
    if [ -z "${done_runs[$run]}" ] && grep -qE "NATIVE_PDF072_COMPLETE|Unhandled exception|Exception:" "$log" 2>/dev/null; then
      done_runs[$run]=1
      echo "END run=$run: $(grep -E 'NATIVE_PDF072_COMPLETE|Unhandled exception|Exception:' "$log" | head -2 | tr '\n' ' ' | cut -c1-300)"
    fi
  done
  [ ${#done_runs[@]} -eq $# ] && exit 0
  sleep 2
done
