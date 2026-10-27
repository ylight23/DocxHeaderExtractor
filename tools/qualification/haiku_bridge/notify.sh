#!/usr/bin/env bash
# One line when unanswered, not-yet-handed-out requests change (count only) and one per finished run.
S="$(cd "$(dirname "$0")" && pwd)"; last=0; ends=0
while true; do
  n=0
  for meta in "$S"/bundles/cohort-*/*/meta.json; do
    b=$(dirname "$meta"); grep -qxF "$b" "$S/dispatched.txt" 2>/dev/null && continue
    [ -e "$b/../../../SUBMITTED_SKIP" ] && continue
    sha=$(basename "$b"); run=$(basename "$(dirname "$b")")
    ls "C:/DocxHeaderExtractor-a99-closed-loop/artifacts/qualification/$run/luna-responses/$sha"*.response.json >/dev/null 2>&1 && continue
    n=$((n+1))
  done
  [ "$n" -gt 0 ] && [ "$n" -ne "$last" ] && echo "WAITING $n new request(s)"
  last=$n
  e=$(grep -c "^END" "$S/cohort-watch.log" 2>/dev/null); [ -z "$e" ] && e=0
  if [ "$e" -gt "$ends" ]; then grep "^END" "$S/cohort-watch.log" | tail -n $((e-ends)); ends=$e; fi
  sleep 20
done
