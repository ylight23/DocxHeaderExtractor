#!/usr/bin/env bash
# Up to N (default 1) cohort bundles with no response that were not handed out yet; marks them handed out. Prints Windows paths.
S="$(cd "$(dirname "$0")" && pwd)"; max=${1:-1}; k=0; touch "$S/dispatched.txt"
for meta in "$S"/bundles/cohort-*/*/meta.json; do
  [ "$k" -ge "$max" ] && break
  b=$(dirname "$meta"); grep -qxF "$b" "$S/dispatched.txt" && continue
  resp=$(python -c "import json,sys;print(json.load(open(sys.argv[1]))['responsePath'])" "$meta")
  [ -e "$resp" ] && continue
  echo "$b" >> "$S/dispatched.txt"; k=$((k+1))
  echo "$(basename "$(dirname "$b")")/$(basename "$b")"
done
