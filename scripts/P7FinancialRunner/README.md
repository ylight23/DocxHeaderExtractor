# P7-D3 Financial Runner Integration — provider-free

Only this command exists; no live mode, keys, metadata HTTP request, provider execution,
Gold read, or downstream execution is enabled:

```
dotnet run --project scripts/P7FinancialRunner -- dry-run <frozen-F1-directory> <new-private-journal-directory> <new-public-receipt.json>
```

The 14 real frozen bodies (7 Control, 7 B) are validated against the original plan and
sent unchanged to a sealed network-free fake. All usage, approval, key and exposure
bound values in this dry run are FIXTURES, not measurements or human authorization.
Synthetic responses are NOT protocol-qualified F1 captures, semantic accuracy evidence,
or permissible downstream inputs. Raw capture completion is not parser ACCEPTED.

Runner gates:

- One-shot, ordered F1-only universe; validate all bodies before any attempt. Snapshot
  the caller's bytes so later mutations cannot alter future requests.
- Require approval receipt, matching execution/financial manifest hashes, approved USD
  cap and dedicated-key receipt. Caller-provided approval/proof attestations are a trusted
  external review boundary, not cryptographic proof of human consent. No live CLI ingests
  or invents these attestations. Actual approval and billing evidence remain absent.
- Require body-specific, endpoint-bound, reviewed all-charge maximum exposure per call.
  The advertised $0.508 stress estimate is not accepted as proof. Fixture bounds cannot
  authorize the non-fixture transport. This runner uses verified-bound reservations,
  NOT the earlier stress-estimate-only financial fixture ledger.
- Check fresh endpoint metadata before each reservation (300 seconds), then check
  remaining total/key budget. Persist create-new reservation before calling transport.
- There is only one private runner dispatch site. Adapter uses exact model/endpoint,
  no redirects, retries 0, no fallback, timeout 300 and frozen bytes. Lazy key acquisition
  happens only after gates and reservation. The adapter is compiled but never instantiated
  by this checkpoint's CLI; production/Web are unchanged.
- Freeze raw evidence before accounting. Missing usage/cost or receipt/storage failure
  halts. Preserve outstanding exposure after unknown settlement, timeout or cancellation;
  never retry, reopen the run, substitute a capture, or release it as a free request.
- Enforce a runner deadline independently of cooperative transport cancellation. A late
  task result cannot settle, be adopted, or fan out more calls. Faults are observed only.
- Record account cost without clamping, not upstream cost; reasoning is not counted twice.
  Complete receipts reconcile exposure but cost/token/budget overruns stop future starts.
- Serialize all 14 slots; halted remainder is NOT_ATTEMPTED, never OTHER or negatives.
  Final receipt failures cannot trigger replay. Reusing a journal directory is rejected.

Raw/body journals contain source excerpts and stay private. Public dry-run receipts expose
only hashes and financial fixture accounting. Old manifests, Gold and receipts stay immutable.
Full capture parser/semantic scoring is deliberately a separate gate and still locked.
This is scoped bypass protection inside this runner, not a claim that arbitrary repository
code with HTTP/key access cannot call a provider. A later live entrypoint must authenticate
approval/proof receipts and exclusively bind its key, durable journal and authorization.
