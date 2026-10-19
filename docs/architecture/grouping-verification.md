# Group verification: qualification-only

Updated 2026-10-19. The verifier-as-authority flow documented in the earlier history of this
file is no longer part of production heading binding. Experiments showed that an independent
semantic verifier can disagree with source-correct grouping, oscillate across repair cycles, and
both falsely accept and falsely reject boundaries. Adding more verifier/repair passes did not
provide a reliable authority.

Production now follows:

1. Selection returns selected source aliases.
2. The grouping model returns contiguous ordered groups.
3. C# validates protocol shape, exact run coverage, alias ownership, contiguity and source order.
4. Only validated groups are bound. A grouping dispute or failed/fallback division remains
   unresolved; it does not rewrite selection membership or trigger a second semantic review.

`AgentGroupVerification` and the historical canary artifacts remain available for qualification
tests and analysis. Their verdicts, boundary suggestions, cycle observations, and ACCEPT states
must not be used to authorize or veto production groups. Strict source identity and protocol
checks remain in force; this rollback removes semantic verifier authority, not deterministic
validation.
