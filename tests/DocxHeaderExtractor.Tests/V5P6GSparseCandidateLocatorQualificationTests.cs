using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6GSparseCandidateLocatorQualificationTests
{
    private const string Root = "artifacts/v5-p6g-sparse-candidate-locator";
    private const int ResponseCap = 49_152;
    private const int MaxInputTokens = 991_808;
    private const int ContextWindowTokens = 1_000_000;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    [Fact]
    public void Freeze_sparse_candidate_contract_registry_audit_validation_and_owned_split_without_provider_or_gold()
    {
        var validation = ValidateFixtures();
        var rows = AuditFull31Directories();
        var maxDirectory = rows.Max(row => row.LocatorDirectoryBytes);
        var maxDelta = rows.Max(row => row.DirectoryPromptDeltaBytes);
        var maxBody = rows.Max(row => row.TotalProviderBodyBytes);
        var maxBoundary = rows.Max(row => row.BoundaryCount);
        var maxOriginal = rows.Max(row => row.OriginalSemanticRequestBytes);
        var maxDeltaPercent = rows.Max(row => row.DeltaPercent);
        var maxModelVisibleContent = rows.Max(row => row.TotalModelVisibleContentBytes);
        var maxSemanticSourceContext = rows.Max(row => row.SemanticSourceContextUtf8Bytes);
        var maxInstructionsSchema = rows.Max(row => row.InstructionsSchemaUtf8Bytes);
        var maxSparseBaseMessage = rows.Max(row => row.P6GBaseUserMessageBytes);
        var ownership = AuditOwnedAtomSplit();
        Assert.Equal(31, rows.Count);
        Assert.Equal(2884, rows.Sum(row => row.OwnedAtoms));
        var maxCompletionBudget = rows.Max(row => row.MaxCompletionTokens);

        FreezeArtifact.AssertJson(Root, "contract.v1.json", new
        {
            schemaVersion = "v5-p6g-sparse-candidate-locator-contract-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            protocol = "v5-heading-occurrence-sparse-locator-1",
            promptComposer = V5SparseCandidateRequestComposerV1.Version,
            responseShape = new { occurrences = "sparse; empty array is valid", occurrence = new { primary = "owned atom A# with optional opaque H# from/to", additionalParts = "ordered owned atom parts with optional opaque boundaries", functions = "unique non-empty subset of the three unary functions" } },
            forbiddenResponseFields = new[] { "sourceAlias", "sourceId", "sourceOrdinal", "text", "verbatimText", "leftExactContext", "rightExactContext", "numericOffset", "start", "end", "occurrence", "claimId", "existingClaimId", "relationTarget", "state", "evidenceNeeds", "PARENT_OF", "REFERENCES", "SAME_ENTITY", "CONTINUES" },
            ownership = "only owned subject atoms get A#/H#; halo remains visible as reasoning context without selectable locators",
            omission = "NO_SEMANTIC_PROPOSAL; later Gold scoring may count it as FN",
            dedupe = "after exact locator resolution by BoundClaimEndpoint.Identity; merge and sort function set; invalid candidates remain quarantined",
            byteCap = new { rawAndCanonicalUtf8Bytes = ResponseCap, overflow = "REJECT_RESPONSE_BEFORE_PARSE_BIND_REPAIR_OR_RETRY" },
            runtime = "UNCHANGED",
        });
        FreezeArtifact.AssertJson(Root, "locator-directory-audit.v1.json", new
        {
            schemaVersion = "v5-p6g-locator-directory-audit-v1", providerCalls = 0, goldRead = false,
            ownedOnly = true, boundaryOffsetsAreRequestSideMetadata = "UTF16_VALID_SCALAR_BOUNDARIES",
            maxBoundaryHandles = maxBoundary, maxRegistryBytes = maxDirectory,
            ownershipCoverage = "all and only each pack's owned subject atoms receive locator handles", rows,
        });
        FreezeArtifact.AssertJson(Root, "response-validation.v1.json", new
        {
            schemaVersion = "v5-p6g-response-validation-v1", providerCalls = 0, goldRead = false,
            fixtureResults = validation,
            invalidLocatorPolicy = "CANDIDATE_LOCAL_QUARANTINE_VALID_SIBLINGS_SURVIVE",
            responseWidePolicy = "ROOT_SHAPE_OR_RAW/CANONICAL_BYTE_OVERFLOW_REJECTS_RESPONSE",
            exactBinding = "existing SemanticSourcePartBinder; unchanged fail-closed authority",
            canonicalDedupe = "AFTER_BIND_BY_BoundClaimEndpoint.Identity_UNION_FUNCTIONS",
        });
        FreezeArtifact.AssertJson(Root, "ownership-split.v1.json", new
        {
            schemaVersion = "v5-p6g-owned-atom-split-v1", providerCalls = 0, goldRead = false,
            policy = "OWNED_ATOM_BINARY_SPLIT_V1", splitDimension = "owned primary atom indices, contiguous deterministic halves",
            invariant = ownership,
            overflow = "finish_reason=length OR raw bytes > cap => do not parse/bind/repair/retry same request; split if >1 owned atoms",
            singletonOverflow = "INCOMPLETE_UNSPLITTABLE_SEMANTIC_OVERFLOW_NOT_EVALUABLE",
            maxTreeNodesFull31 = 2L * 2884 - 31,
            formula = "per pack 2n-1; full cohort sum=2*2884-31=5737",
            note = "hard structural node upper bound, not expected or authorized provider call count",
        });
        FreezeArtifact.AssertJson(Root, "full31-request-size.v1.json", new
        {
            schemaVersion = "v5-p6g-full31-request-size-v1", providerCalls = 0, goldRead = false,
            packCount = rows.Count, ownedAtomTotal = rows.Sum(row => row.OwnedAtoms),
            originalSemanticRequestBytesMax = maxOriginal,
            locatorDirectoryBytesMax = maxDirectory,
            directoryPromptDeltaBytesMax = maxDelta,
            directoryDeltaPercentMax = maxDeltaPercent,
            maxBoundaryHandles = maxBoundary, maxTotalProviderRequestBytes = maxBody,
            maxModelVisibleContentUtf8Bytes = maxModelVisibleContent,
            limits = new { requestSize = "MEASURED_ONLY", providerRequestHardLimitAudit = "No authoritative provider/model request-size threshold is asserted by this provider-free audit." },
            rows,
        });
        FreezeArtifact.AssertJson(Root, "gate.v1.json", new
        {
            schemaVersion = "v5-p6g-gate-v1",
            SPARSE_CANDIDATE_PROTOCOL = "CLOSED",
            LOCATOR_DISCOVERABILITY = "PASS",
            MODEL_CONTEXT_LIMIT = new { status = "AUTHORITY_FOUND", maxInputTokens = MaxInputTokens, contextWindowTokens = ContextWindowTokens },
            ACTUAL_MAX_INPUT_TOKENS = "NOT_MEASURED_NO_VERIFIED_OFFLINE_TOKENIZER",
            HTTP_BODY_BYTE_LIMIT = "NOT_ESTABLISHED",
            REQUEST_EFFICIENCY = "POOR_MEASURED_ONLY",
            requestEfficiencyMeasurements = new { maxProviderBodyUtf8Bytes = maxBody, maxModelVisibleContentUtf8Bytes = maxModelVisibleContent, maxLocatorDirectoryUtf8Bytes = maxDirectory, locatorOverheadPercentVsSameSparsePrompt = maxDeltaPercent },
            OWNED_ATOM_SPLIT = "PASS",
            PROVIDER_MANIFEST = "NOT_PREPARED",
            PROVIDER_CALLS = 0,
            GOLD_READ = false,
            SHARED_RUNTIME = "UNCHANGED",
            nextGate = "P6H_TOKEN_COUNT_NOT_MEASURED; obtain an exact model tokenizer/chat-template artifact before evaluating context capacity; provider execution remains blocked",
        });
        FreezeArtifact.AssertJson("artifacts/v5-p6h-request-budget", "request-budget.v1.json", new
        {
            schemaVersion = "v5-p6h-request-budget-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", provider = "alibaba", reasoning = "none" },
            authority = new
            {
                maxInputTokens = MaxInputTokens,
                contextWindowTokens = ContextWindowTokens,
                maxOutputTokens = 131072,
                nonThinkingMode = true,
                source = "https://docs.modelstudio.console.alibabacloud.com/en/model-studio/qwen3-7-flash",
                appliedInputTokenCeiling = "991808",
                reservedOutputTokensMax = maxCompletionBudget,
            },
            tokenizer = new
            {
                exactOfflineTokenizer = "NOT_AVAILABLE_IN_REPOSITORY",
                actualInputTokens = "NOT_MEASURED",
                perComponentTokenCounts = "NOT_MEASURED",
                tokenizerEvidenceSource = "https://help.aliyun.com/en/model-studio/text-generation",
                reason = "Alibaba documents that tokenization is model-specific and server chat-template tokens count; actual usage is returned by an inference response. No verified Qwen3.7 Flash tokenizer/chat-template asset is present locally; obtaining usage by request would violate ProviderCalls=0.",
            },
            byteMeasurementsAreNotTokenEstimates = true,
            componentTokenCounts = new { semanticSourceContext = (int?)null, locatorDirectory = (int?)null, instructionsSchema = (int?)null, totalModelVisibleInput = (int?)null },
            maxMeasuredUtf8Bytes = new
            {
                providerHttpBody = maxBody,
                modelVisibleSystemPlusUserContent = maxModelVisibleContent,
                semanticSourceContext = maxSemanticSourceContext,
                locatorDirectory = maxDirectory,
                instructionsSchema = maxInstructionsSchema,
                sameSparsePromptWithoutLocatorDirectory = maxSparseBaseMessage,
                locatorDirectoryDelta = maxDelta,
                locatorDirectoryDeltaPercent = maxDeltaPercent,
                originalLegacySemanticRequest = maxOriginal,
            },
            rows,
            gate = "CONTEXT_CAPACITY_NOT_EVALUABLE_UNTIL_EXACT_TOKENIZER_IS_AVAILABLE; PROVIDER_EXECUTION_BLOCKED",
        });
    }

    private static IReadOnlyList<object> ValidateFixtures()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0", "src0", 0, 1, 0, 0, "Alpha 😀 Beta"),
            new SemanticSourceAtom("L1", "src1", 1, 1, 1, 0, "Gamma"),
            new SemanticSourceAtom("L2", "src2", 2, 1, 2, 0, "Halo"),
        };
        var registry = RequestLocalLocatorRegistry.Create(atoms.Take(2).ToArray()); // halo has no selectable locator
        var owned = (IReadOnlySet<int>)new HashSet<int>([0, 1]);
        Assert.Throws<InvalidOperationException>(() => registry.AtomHandle(2));
        Assert.DoesNotContain("Halo", JsonSerializer.Serialize(registry.Directory(), CanonicalJson.Options), StringComparison.Ordinal);
        var a0 = registry.AtomHandle(0); var a1 = registry.AtomHandle(1);
        var b0 = registry.BoundaryHandle(0, 0); var b5 = registry.BoundaryHandle(0, 5);
        var bEnd = registry.BoundaryHandle(0, atoms[0].Text.Length);
        var bEmojiMid = "H999"; // cannot be issued: surrogate interior
        var validWhole = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""";
        var validStrict = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b0}}","to":"{{b5}}"},"additionalParts":[],"functions":["DOCUMENT_IDENTITY"]}]}""";
        var validMultipart = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b0}}","to":"{{b5}}"},"additionalParts":[{"atom":"{{a1}}"}],"functions":["NAVIGATION_REPRESENTATION"]}]}""";
        var unicodeBoundary = registry.BoundaryHandle(0, 8); // after emoji, at a valid scalar boundary
        var unicode = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{unicodeBoundary}}","to":"{{bEnd}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""";
        var siblingMixed = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]},{"primary":{"atom":"A99"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""";
        var duplicateFunctions = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION","STRUCTURAL_REGION"]}]}""";
        var nonCanonicalAtom = """{"occurrences":[{"primary":{"atom":"A00"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""";
        var unknownFunction = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["HEADING"]}]}""";
        var duplicateLocator = $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]},{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["NAVIGATION_REPRESENTATION"]}]}""";
        var cases = new List<object>();
        void Accepted(string name, string raw, int expected = 1)
        {
            using var json = JsonDocument.Parse(raw); var result = registry.Parse(json.RootElement, Encoding.UTF8.GetByteCount(raw), ResponseCap, owned);
            Assert.Equal(expected, result.Response.Occurrences.Count); cases.Add(new { name, outcome = "ACCEPT", normalizedOccurrences = result.Response.Occurrences.Count, quarantined = result.Quarantined.Count });
        }
        void Quarantined(string name, string raw)
        {
            using var json = JsonDocument.Parse(raw); var result = registry.Parse(json.RootElement, Encoding.UTF8.GetByteCount(raw), ResponseCap, owned);
            Assert.Empty(result.Response.Occurrences); Assert.Single(result.Quarantined); cases.Add(new { name, outcome = "QUARANTINE_CANDIDATE", reason = result.Quarantined[0].Reason });
        }
        Accepted("whole-atom", validWhole);
        Accepted("strict-substring", validStrict);
        Accepted("multipart", validMultipart);
        Accepted("unicode-scalar-boundary", unicode);
        cases.Add(new { name = "halo-visible-without-selectable-locator", outcome = "NO_HALO_ATOM_HANDLE_ISSUED" });
        Quarantined("unknown-atom", $$"""{"occurrences":[{"primary":{"atom":"A99"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("unissued-noncanonical-atom-spelling", nonCanonicalAtom);
        Quarantined("unknown-boundary-handle", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b0}}","to":"{{bEmojiMid}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("cross-atom-boundary", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b0}}","to":"{{registry.BoundaryHandle(1, 1)}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("missing-boundary-side", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("from-not-before-to", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b5}}","to":"{{b0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("full-span-boundaries", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}","from":"{{b0}}","to":"{{bEnd}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("multipart-reverse-order", $$"""{"occurrences":[{"primary":{"atom":"{{a1}}"},"additionalParts":[{"atom":"{{a0}}"}],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("duplicate-additional-atom", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[{"atom":"{{a1}}"},{"atom":"{{a1}}"}],"functions":["STRUCTURAL_REGION"]}]}""");
        Quarantined("copied-text-field-forbidden", $$"""{"occurrences":[{"primary":{"atom":"{{a0}}"},"additionalParts":[],"functions":["STRUCTURAL_REGION"],"text":"Alpha 😀 Beta"}]}""");
        Quarantined("unknown-function", unknownFunction);
        Quarantined("duplicate-function", duplicateFunctions);
        Accepted("empty-occurrences", "{\"occurrences\":[]}", 0);
        using (var mixed = JsonDocument.Parse(siblingMixed))
        {
            var result = registry.Parse(mixed.RootElement, Encoding.UTF8.GetByteCount(siblingMixed), ResponseCap, owned);
            Assert.Single(result.Response.Occurrences); Assert.Single(result.Quarantined);
            cases.Add(new { name = "valid-sibling-invalid-sibling", outcome = "VALID_SIBLING_SURVIVES", quarantined = result.Quarantined.Count });
        }
        using (var duplicate = JsonDocument.Parse(duplicateLocator))
        {
            var result = registry.Parse(duplicate.RootElement, Encoding.UTF8.GetByteCount(duplicateLocator), ResponseCap, owned);
            Assert.Single(result.Response.Occurrences);
            Assert.Equal(new[] { "NAVIGATION_REPRESENTATION", "STRUCTURAL_REGION" }, result.Response.Occurrences[0].Functions);
            cases.Add(new { name = "exact-duplicate-merge-functions", outcome = "DEDUPED_AFTER_BIND_UNION_FUNCTIONS", mergedFunctions = result.Response.Occurrences[0].Functions });
        }
        using (var empty = JsonDocument.Parse("{\"occurrences\":[]}"))
            Assert.Throws<InvalidOperationException>(() => registry.Parse(empty.RootElement, ResponseCap + 1, ResponseCap, owned));
        cases.Add(new { name = "raw-response-byte-overflow", outcome = "RESPONSE_REJECT_BEFORE_CANDIDATE_PARSE_BIND" });
        using (var badRoot = JsonDocument.Parse("[]"))
            Assert.Throws<InvalidOperationException>(() => registry.Parse(badRoot.RootElement, 2, ResponseCap, owned));
        using (var duplicateRootKey = JsonDocument.Parse("{\"occurrences\":[],\"occurrences\":[]}"))
            Assert.Throws<InvalidOperationException>(() => registry.Parse(duplicateRootKey.RootElement, 31, ResponseCap, owned));
        cases.Add(new { name = "malformed-root", outcome = "RESPONSE_REJECT" });
        return cases;
    }

    private static IReadOnlyList<DirectoryAuditRow> AuditFull31Directories()
    {
        var rows = new List<DirectoryAuditRow>();
        foreach (var (id, pdf, expected) in new[] { ("SRC-089", SourcePdfCorpus.Src089, 7), ("SRC-095", SourcePdfCorpus.Src095, 24) })
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), id, Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            Assert.Equal(expected, packs.Count);
            var sourceAtoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf)).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            foreach (var (pack, ordinal) in packs.Select((item, index) => (item, index + 1)))
            {
                var ownedAtoms = pack.OwnedAliases.Select(alias => sourceAtoms[alias]).ToArray();
                Assert.Equal(pack.OwnedAliases.Count, ownedAtoms.Length);
                var registry = RequestLocalLocatorRegistry.Create(ownedAtoms);
                var directory = registry.Directory();
                Assert.Equal(ownedAtoms.Length, directory.Atoms.Count);
                var sparsePrompt = V5SparseCandidateRequestComposerV1.Compose(Contract, pack.Packet, registry);
                Assert.DoesNotContain("semanticRequest", sparsePrompt.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("PARENT_OF", sparsePrompt.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("sourceAlias", sparsePrompt.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("sourceOrdinal", sparsePrompt.UserMessage, StringComparison.Ordinal);
                using (var message = JsonDocument.Parse(sparsePrompt.UserMessage))
                {
                    Assert.Equal(ownedAtoms.Length, message.RootElement.GetProperty("ownedSubjects").GetArrayLength());
                    Assert.Equal(pack.Packet.ContextOnlyEvidence.Count, message.RootElement.GetProperty("contextOnlyEvidence").GetArrayLength());
                    Assert.All(message.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray(), node =>
                    {
                        Assert.False(node.TryGetProperty("atom", out _));
                        Assert.False(node.TryGetProperty("boundaries", out _));
                    });
                }
                var maxTokens = pack.MaxCompletionTokens;
                var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(sparsePrompt.SystemPrompt, sparsePrompt.UserMessage, maxTokens, Envelope);
                rows.Add(new DirectoryAuditRow(id, ordinal, ownedAtoms.Length, pack.Request.Utf8Bytes,
                    sparsePrompt.LocatorDirectoryUtf8Bytes, sparsePrompt.UserMessageUtf8Bytes - sparsePrompt.UserMessageWithoutLocatorDirectoryUtf8Bytes,
                    sparsePrompt.UserMessageWithoutLocatorDirectoryUtf8Bytes,
                    directory.Atoms.Sum(atom => atom.Boundaries.Count), sparsePrompt.LocatorDirectoryUtf8Bytes,
                    body.Bytes, sparsePrompt.UserMessageWithoutLocatorDirectoryUtf8Bytes == 0 ? 0 :
                        (double)(sparsePrompt.UserMessageUtf8Bytes - sparsePrompt.UserMessageWithoutLocatorDirectoryUtf8Bytes) * 100 / sparsePrompt.UserMessageWithoutLocatorDirectoryUtf8Bytes,
                    sparsePrompt.SystemPromptUtf8Bytes + sparsePrompt.UserMessageUtf8Bytes,
                    sparsePrompt.SystemPromptUtf8Bytes, sparsePrompt.UserMessageUtf8Bytes, sparsePrompt.SemanticSourceContextUtf8Bytes,
                    sparsePrompt.InstructionsSchemaUtf8Bytes, maxTokens, Math.Min(MaxInputTokens, ContextWindowTokens - maxTokens),
                    sparsePrompt.UserMessageSha256, body.Hash, null, null, null, null, null));
            }
        }
        return rows;
    }

    private static object AuditOwnedAtomSplit()
    {
        var parent = OwnedAtomShardDomain.Full(96);
        var (left, right) = OwnedAtomShardSplitter.Split(parent);
        Assert.Empty(left.OwnedAtomIndices.Intersect(right.OwnedAtomIndices));
        Assert.Equal(parent.OwnedAtomIndices.Order().ToArray(), left.OwnedAtomIndices.Concat(right.OwnedAtomIndices).Order().ToArray());
        var first = new OccurrenceLocator(new OccurrenceLocatorPart("A10", "H1", "H2"), [], ["STRUCTURAL_REGION"]);
        var multi = new OccurrenceLocator(new OccurrenceLocatorPart("A10"), [new OccurrenceLocatorPart("A70")], ["STRUCTURAL_REGION"]);
        Assert.Equal(1, new[] { left, right }.Count(shard => shard.Owns(first)));
        Assert.Equal(1, new[] { left, right }.Count(shard => shard.Owns(multi)));
        Assert.Equal("INCOMPLETE_UNSPLITTABLE_SEMANTIC_OVERFLOW", OwnedAtomShardSplitter.OnUnsafeResponse(new("leaf", [4]), "length", 100, ResponseCap).Status);
        return new { parentOwned = parent.OwnedAtomIndices.Count, childCoverage = left.OwnedAtomIndices.Count + right.OwnedAtomIndices.Count, intersection = left.OwnedAtomIndices.Intersect(right.OwnedAtomIndices).Count(), strictSubstringOwner = "PRIMARY_A10_CHILD", multipartOwner = "PRIMARY_A10_CHILD_ADDITIONAL_A70_DOES_NOT_CHANGE_OWNER", singletonOverflow = "INCOMPLETE_UNSPLITTABLE_SEMANTIC_OVERFLOW" };
    }

    private sealed record DirectoryAuditRow(string DocumentId, int ParentOrdinal, int OwnedAtoms, int OriginalSemanticRequestBytes,
        int LocatorDirectoryBytes, int DirectoryPromptDeltaBytes, int P6GBaseUserMessageBytes, int BoundaryCount, int RegistryBytes, int TotalProviderBodyBytes, double DeltaPercent,
        int TotalModelVisibleContentBytes, int SystemPromptUtf8Bytes, int UserMessageUtf8Bytes, int SemanticSourceContextUtf8Bytes,
        int InstructionsSchemaUtf8Bytes, int MaxCompletionTokens, int EffectiveInputTokenCeilingWithOutputReserve,
        string UserMessageSha256, string ProviderBodySha256, int? ActualInputTokens,
        int? SemanticSourceContextTokens, int? LocatorDirectoryTokens, int? InstructionsSchemaTokens, int? TotalModelVisibleInputTokens);
}
