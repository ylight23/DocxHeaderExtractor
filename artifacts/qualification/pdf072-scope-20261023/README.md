# PDF 072: selection scope canary

Qualification only; productionAuthorization=false. No production code, heading writeback or Gold changed.

## Offline replay

Rebuilt first requests with the production source/parser and selection task at 8b1e09a. For each request, repeat construction and AgentUnit.FirstRequestSha256 agree. All 63 historical decisions across SEG-014/015/016 parse successfully and match both initial and final audit decisions (zero C# changes).

The historical Web NDJSON does NOT retain request bodies or hashes. These are reproducible reconstructed packets, not proof of byte identity with the historical transport. The canary itself stores actual sent request bodies and hashes; all eight first-request hashes match the precomputed packets.

## Predeclared intervention

A: production SEG-014/015/016 (15, 32, 16 targets). B: replace SEG-015 target scope with whole page 15 (76 atoms), retaining production source, facts, +/-20 context, document styles, instruction/system prompt, tools, model/provider and token cap. Only temporary qualification harness changes scope. B request is 103604 bytes, within the unchanged 131072 byte limit; A requests are 38400/56127/38324 bytes.

Predeclared sequence: block 1 A14,A15,A16,B15; block 2 B15,A14,A15,A16. No decisions from previous calls sent to later calls; no semantic feedback or hints about the four peer headings. Production tool/protocol conversation retained. Every conversation finished COMPLETE in one POST, finish=stop; no tools or repairs used.

## Results

| Peer | Historical A | Block1 A | Block1 B | Block2 A | Block2 B |
|---|---|---|---|---|---|
| L0557:S0 — ICP Technical Advisory Group members | SELECT | SELECT | SELECT | SELECT | SELECT |
| L0566:S0 — ICP experts, guest speakers, and observers | NOT_SELECT | NOT_SELECT | SELECT | SELECT | SELECT |
| L0576:S0 — ICP Inter-Agency Coordination Group (IACG) | NOT_SELECT | NOT_SELECT | SELECT | SELECT | SELECT |
| L0587:S0 — World Bank | NOT_SELECT | NOT_SELECT | SELECT | NOT_SELECT | SELECT |
| Selected peers | 1/4 | 1/4 | 4/4 | 3/4 | 4/4 |

## Interpretation

The model's output varies even for byte-identical A15 requests. Scope intervention B selects all four peers in both calls; the narrower A16 rejects World Bank in both calls. This supports scope-sensitive model behavior and a possible contribution from production packing. It does not establish a serializer/parser defect or scope as the sole cause.

B changes target membership, context composition, facts coverage and packet size together, and one B conversation replaces three A conversations. This experiment measures the combined request-scope intervention, not one isolated factor. Two blocks on one page are exploratory local source adjudication, not Strict Gold or broad accuracy qualification. Other selected lines must be reviewed before any production promotion.

## Evidence

See manifest.json for source/universe/evidence/instruction/system and request hashes, offline-integrity.json for field invariants and historical decision consistency, integrated-summary.json for actual transport-body hashes, and block*.trace.json/outcome.json for raw model turns and parsed outcomes. Canary.cs.txt is the temporary harness source; it runs through the production AgentUnit and AgentModel. All artifacts contain source text; no credentials are saved.

Total live usage recorded by transport: 0.006222149 USD across 8 POSTs.
