# ARCH-4F SourceDocument Authority Cutover

The normal DOCX authority route now obtains `DocxSourceExtractionResult`
once and passes both views explicitly: the immutable `SourceDocument` is the
source-fact authority, while `SlimDocument` is a compatibility sidecar for
policy and deferred demotion state.

`DocxAuthorityPipeline` builds its normal source blocks, display text, source
identity, source style/layout values, and output text from `SourceDocument`.
The Slim sidecar remains visible only where the existing deferred demotion or
numbering/style compatibility boundary still requires it. These remaining two
reads are documented and have no unexplained source-authority ambiguity.

The old Slim-only overload remains as an adapter for legacy/internal callers;
it projects Slim into SourceDocument and does not change their behavior. The
normal `AuthorityExtractionPipeline` uses the dual-source overload directly.
No demotion, candidate, ranking, validator, hierarchy, route-policy, or
provider semantics were changed. Source identity/fact and policy deltas are
zero, F regression is `2/2`, Release build is `PASS`, and provider calls are
zero.
