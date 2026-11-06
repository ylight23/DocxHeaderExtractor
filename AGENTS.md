# Project diagnostic runs

- For heading-pipeline diagnostic or qualification runs, preserve the raw model responses for SELECT, GROUP, PLACE, and any repair calls actually made, alongside per-attempt stage, unit, finish reason, and protocol errors. Verify the evidence was written before reporting the run complete; explicitly report any unavailable response or failed capture.
- Store request and response evidence only in a local, non-committed run directory because it may contain document text. Do not expose credentials. This requirement does not change production logging defaults or authorize provider calls.
