# S6-S10: residual dependency and vocabulary closure

Baseline: `bdcdb8c`. These checkpoints do not reopen structural authority design.
Gold, provider prompts/bodies, frozen artifacts and research H3 are outside scope.

## S6: factory input ownership

IInferenceTransportFactory, Infrastructure and Web factories now take only an optional
CancellationToken. PipelineOptions was unused beyond null-check/delegation; provider
selection remains constructor-owned. No replacement configuration DTO is introduced.
DOCX/PDF callers retain their own pipeline options and forward the same cancellation token.
PDF provider authorization, capability rejection and transport ownership are unchanged.

This is an intentional public C# signature change; callers/implementers must remove the
PipelineOptions argument. No legacy overload is retained. No wire contract is affected.
InferenceFactoryOwnershipTests checks both signatures and absence of the reverse dependency;
PdfProductionProviderPolicyTests retains provider-policy behavior coverage without network calls.
