# Round-3 Cumulative Regression

Status: `PASS`

The first cumulative run at LEGACY-6 head found one real new failure. The
failure was not a test-inventory change: the same test existed in the Round-2
canonical revision and in the Round-3 LEGACY-6 head.

Initial execution revision: `03e5a0fd29554cab54d01eeb410f3b300ddf0990`

Initial full suite: `1338 total / 1307 passed / 31 failed / 0 skipped`

New failure:

```text
DocxHeaderExtractor.Tests.TrailingBlockTests.Nhan_khoi_chu_ky_mang_style_Heading_van_bi_ha_khi_style_khong_dang_tin
```

Regression owner: `LEGACY-5 ordered demotion cutover`

Regression cause: `DocxSlimExtractor` counted structural markers after
`StyleTrust` cleared built-in heading style policy state. That lost the
pre-clear structural evidence needed by the ordered demotion rules.

Fix revision: `32bb000343361516078f37da63943e5073b678fe`

Fix scope: one source file. Test expectations were not changed.

Canonical execution revision: `32bb000343361516078f37da63943e5073b678fe`

Canonical full suite: `1338 total / 1308 passed / 30 failed / 0 skipped`

Reconciliation against the Round-2 canonical failure universe:

```ini
STILL_FAILING = 30
RESOLVED = 0
NEW_FAILURES = 0
CHANGED_FINGERPRINTS = 0
UNJOINED = 0
FAILURE_UNIVERSE_FROZEN = true
FULL_SUITE_VALID_FOR_FREEZE = true
```

Join method: `EXACT_FQN_PLUS_PRESENTATION_INSENSITIVE_NORMALIZED_FAILURE`.

Release build passed with `0` errors. Focused regression checks passed before
the canonical full-suite rerun:

```text
single regression FQN: PASS
TrailingBlockTests: 2/2 PASS
LEGACY focused superset: 61/61 PASS
```

Provider calls: `0`.

Raw TRX policy: `DO_NOT_PUSH_RAW_TRX`. The canonical raw TRX remains local:

```text
C:/DocxHeaderExtractor-round3-fix-fullsuite/tests/DocxHeaderExtractor.Tests/TestResults/round3-fix-cumulative-full-suite.trx
```

Raw TRX SHA-256:

```text
083E57E41529511EF6781A06EDD770005B481FE6DD1A021E1A20640047226620
```

The publication commit for this artifact is intentionally later than the
canonical execution revision. Do not rewrite the execution revision to the
publication SHA.

Next: `LEGACY-9` removal-readiness decision. Physical Slim removal is not
authorized by this gate; the remaining compatibility `Extract()` callers must
be audited first.
