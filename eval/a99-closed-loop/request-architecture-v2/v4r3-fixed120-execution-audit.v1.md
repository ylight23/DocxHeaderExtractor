# V4R3 FIXED120 execution audit

Status: `INCOMPLETE_EXPERIMENT_ONLY`

The provider-authorized run completed its 25 primary calls, then executed the
frozen bounded recovery protocol. Gold and scoring remain closed.

```text
primary calls                 25
primary contract-valid        16
primary retryable              9
recovery calls                23
identical retries              9
adaptive child calls          14
recovery contract-valid        8
completed parent sets        4 / 9 pending parents
accepted parent coverage     20 / 25
total provider calls         48
```

Pending parent ownership sets are ordinals `004`, `006`, `017`, `020`, and
`021`. The frozen preflight allowed 12 adaptive child calls; the execution
required 14, so no further provider call is permitted without a new
adjudicated preflight. The global hard cap of 55 was not exceeded.

No combined lineage or scoring authority was created. P05 artifacts and the
V4R3 preflight remain unchanged.
