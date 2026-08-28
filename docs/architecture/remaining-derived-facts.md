# ARCH-4D2 Remaining Derived Facts Ownership

ARCH-4D2 closes the ownership decision for all ten `DERIVED_FACT` entries in
the ARCH-4A inventory. Two are now owned by the neutral feature derivation
component: `BodyFontSizePt` and `Corrupt`. The remaining eight have explicit
boundaries and are intentionally deferred.

`HasBuiltInHeadingStyle` is policy-adjacent because it participates in styled
heading selection and style-trust demotion. `TableRole`, `PrecedesTable`, and
the TOC-related fields belong to table/TOC structural feature boundaries.
`NumberingDepth` and `NumberingStyleLevel` wait for a numbering boundary that
retains the numbering definition graph. `DefaultFontSizePt` waits for the DOCX
style-source boundary because the current immutable source contract does not
yet include `docDefaults` and resolved style defaults.

This is an ownership closure, not a request to move every field immediately.
There is no generic deriver containing TOC, table-role, heading-policy, or
numbering inference. Slim still retains deferred derived responsibility, while
every ARCH-4A derived field has an explicit owner or defer boundary.

Provider calls: 0. Production behavior change: false.
