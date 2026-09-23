using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The authority dimension a universe hash does not cover: what the model is actually shown.
/// <para>
/// A migration can leave every alias, its text and its order untouched while changing the facts
/// attached to each one, the context around it, or how it is cut into requests. A preflight that
/// checked only the universe would pass while the model read something else entirely. The previous
/// migration attempt was blocked on a related confusion, and the gap it exposed is what this file
/// pins: coordinates, evidence, packing and requests are four hashes, not one.
/// </para>
/// <para>
/// Shadow throughout. The active lane still builds its universe from parser blocks, canonical Gold
/// is untouched, and no request leaves the process.
/// </para>
/// </summary>
public sealed class PdfStructuredSourceAuthorityBuilderTests
{
    private const string Doc0252 = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Artifacts = "eval/a99-closed-loop/representation";
    private const int Atoms = 650;
    private const int ApprovedHeadings = 41;

    /// <summary>A 467-unit universe of blocks over segment lines. Never an atom universe.</summary>
    private const string BlockDiagnosticHash =
        "3b351411e1c773d14bf534f19c4ae721612716a4561396c757bc3ac35ed8b05c";

    /// <summary>The atom catalog serialized through the generic alias catalog, which renumbers.</summary>
    private const string CandidateSegmentHash =
        "86cad7d6b7a52391b4361ea1ad402774fa4bccf6af5570e3705f7582a6893c79";

    private const string Doc0252SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    /// <summary>
    /// The request-plan hash a hand-rolled measurement helper produced, before this file's own
    /// composer discovery showed it disagreed with production. Kept as a named predecessor, never
    /// reused as authority.
    /// </summary>
    private const string PredecessorSyntheticRequestPlanHash =
        "97952bd190c4ffffb77f30e12924efaf648c92d840228169a0c9adec74dd6d1d";

    // ---- every atom the model sees can be addressed ---------------------------------------------

    [Fact]
    public void Every_atom_has_both_a_coordinate_and_evidence()
    {
        // The defect that blocked the migration: candidate contexts were keyed by layout block id
        // while aliases were segments, so the lookup matched nothing and the model would have been
        // sent empty evidence with every hash still green.
        var plan = Plan();

        Assert.Equal(Atoms, plan.Atoms.Count);
        Assert.Equal(Atoms, plan.Evidence.Count);

        var catalog = plan.Atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Atoms, catalog.Count);
        Assert.All(plan.Evidence, item => Assert.Contains(item.SourceAlias, catalog));
        Assert.All(plan.Evidence, item => Assert.False(string.IsNullOrWhiteSpace(item.ExactSourceText)));
        Assert.Empty(plan.Packs.Where(pack => pack.OwnedAliases.Count == 0));

        // Both keys reach the same atom, and they are one namespace rather than two: the evidence
        // is addressed by the alias, the context by the source id, and every atom has exactly one
        // of each. A layout block id is never either of them - that separation is what stops the
        // old coordinate authority from reappearing through a lookup that happens to succeed.
        var sourceIds = plan.Atoms.Select(atom => atom.SourceId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Atoms, sourceIds.Count);
        Assert.Equal(
            plan.Atoms.Select(atom => atom.SourceId),
            plan.Evidence.Select(item => item.SourceId));
        Assert.All(plan.LayoutBlockByAtom.Values, block =>
        {
            Assert.DoesNotContain(block, catalog);
            Assert.DoesNotContain(block, sourceIds);
        });
    }

    [Fact]
    public void Every_atom_carries_a_layout_block_label_and_keeps_its_own_alias()
    {
        var plan = Plan();

        Assert.All(plan.Atoms, atom => Assert.True(
            plan.LayoutBlockByAtom.ContainsKey(atom.SourceId),
            $"{atom.Alias} has no layout block to be shown with"));

        // Several atoms share a block, and none of them inherits its address.
        var shared = plan.Atoms
            .GroupBy(atom => plan.LayoutBlockByAtom[atom.SourceId], StringComparer.Ordinal)
            .First(group => group.Count() > 1)
            .ToArray();
        Assert.Equal(shared.Length, shared.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Regrouping_the_layout_moves_the_evidence_and_leaves_the_coordinates_alone()
    {
        // The property the rearrangement rests on. Blocks decide what the model is told about
        // where text sits; they no longer decide what any of it is called.
        var segments = Segments();
        var continuation = PdfStructuredSourceAuthorityBuilder.Build(segments, PdfBlockGrouping.ContinuationV2);
        var legacy = PdfStructuredSourceAuthorityBuilder.Build(segments, PdfBlockGrouping.LegacyV1);

        Assert.Equal(continuation.SourceAliasUniverseHash, legacy.SourceAliasUniverseHash);
        Assert.Equal(
            continuation.Atoms.Select(atom => atom.Alias),
            legacy.Atoms.Select(atom => atom.Alias));
        Assert.NotEqual(continuation.ModelVisibleEvidenceHash, legacy.ModelVisibleEvidenceHash);

        // The consequence: different evidence composes into different requests, through the one
        // real composer, with no request-specific logic of its own to keep in step.
        Assert.NotEqual(
            string.Concat(ComposedRequests(continuation)),
            string.Concat(ComposedRequests(legacy)));
    }

    [Fact]
    public void The_plan_is_deterministic()
    {
        var segments = Segments();
        var first = PdfStructuredSourceAuthorityBuilder.Build(segments);
        var second = PdfStructuredSourceAuthorityBuilder.Build(segments);

        Assert.Equal(first.SourceAliasUniverseHash, second.SourceAliasUniverseHash);
        Assert.Equal(first.ModelVisibleEvidenceHash, second.ModelVisibleEvidenceHash);
        Assert.Equal(first.CallPlanHash, second.CallPlanHash);
        Assert.Equal(ComposedRequests(first), ComposedRequests(second));
    }

    [Fact]
    public void The_atoms_conserve_the_text_of_the_segments_they_came_from()
    {
        var segments = Segments();
        var plan = PdfStructuredSourceAuthorityBuilder.Build(segments);

        Assert.Equal(
            segments.Select(line => line.Projection.VerbatimText),
            plan.Atoms.Select(atom => atom.Text));
        Assert.Equal(
            plan.Atoms.Select(atom => atom.Text),
            plan.Evidence.Select(item => item.ExactSourceText));
    }

    // ---- what a request would contain ------------------------------------------------------------

    [Fact]
    public void No_request_exposes_an_address_that_is_not_an_atom()
    {
        var plan = Plan();
        var catalog = plan.Atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        var blockIds = plan.LayoutBlockByAtom.Values.ToHashSet(StringComparer.Ordinal);
        var requests = ComposedRequests(plan);
        Assert.Equal(plan.Packs.Count, requests.Count);

        foreach (var pack in plan.Packs)
        {
            Assert.All(pack.OwnedAliases, alias => Assert.Contains(alias, catalog));
            Assert.All(pack.VisibleAliases, alias => Assert.Contains(alias, catalog));

            // A block id appearing where an alias belongs would put the old coordinate authority
            // back into the request without anything else changing.
            Assert.All(pack.OwnedAliases, alias => Assert.DoesNotContain(alias, blockIds));
        }

        Assert.All(requests, request =>
        {
            Assert.Contains("\"ownedSourceAliases\"", request);
            Assert.Contains("sourceParts", request);
        });

        Assert.Equal(
            plan.Atoms.Select(atom => atom.Alias),
            plan.Packs.SelectMany(pack => pack.OwnedAliases));
    }

    [Fact]
    public void The_two_atoms_of_the_wrapped_heading_are_both_visible_and_still_bind()
    {
        // S0616, the case that could not be represented under block authority. Both atoms reach
        // the model, and the binder resolves them without a block being consulted anywhere.
        var plan = Plan();
        var first = plan.Atoms.Single(atom => atom.Alias == "L0359:S0");
        var second = plan.Atoms.Single(atom => atom.Alias == "L0360:S0");

        Assert.StartsWith("2. A Survey Based Approach", first.Text, StringComparison.Ordinal);
        Assert.Equal("Comparisons", second.Text);

        var requests = ComposedRequests(plan);
        var visible = plan.Packs
            .Select((pack, index) => (pack, index))
            .Where(item => item.pack.OwnedAliases.Contains(first.Alias) || item.pack.OwnedAliases.Contains(second.Alias))
            .Select(item => requests[item.index])
            .ToArray();
        Assert.All([first, second], atom => Assert.Contains(visible,
            request => request.Contains($"\"alias\":\"{atom.Alias}\"", StringComparison.Ordinal)));

        var bound = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(
        [
            new SemanticSourcePart(first.Alias, CanonicalSemanticSelectionMode.WholeAlias),
            new SemanticSourcePart(second.Alias, CanonicalSemanticSelectionMode.WholeAlias),
        ]));

        Assert.True(bound.IsBound);
        Assert.Equal(SemanticSourceLocality.NextRowCompatible, bound.Parts[1].LocalityFromPrevious);

        // Independence, shown by varying the thing it is supposed to be independent of. Over
        // segment lines both groupings happen to put these two atoms in one block - the "." that
        // used to divide them is back where it belongs - so the demonstration is that the binding
        // is identical under either grouping, and that the binder is handed atoms and never sees a
        // block at all. The case where these two were separate occurrences is the active universe,
        // where they are S0616 and S0618; that is recorded in doc-0252-structured-source-parts.
        var legacy = PdfStructuredSourceAuthorityBuilder.Build(Segments(), PdfBlockGrouping.LegacyV1);
        Assert.NotEqual(plan.ModelVisibleEvidenceHash, legacy.ModelVisibleEvidenceHash);
        Assert.Equal(bound.Identity, SemanticSourcePartBinder.Bind(legacy.Atoms,
            new SemanticSourcePartsProposal(
            [
                new SemanticSourcePart(first.Alias, CanonicalSemanticSelectionMode.WholeAlias),
                new SemanticSourcePart(second.Alias, CanonicalSemanticSelectionMode.WholeAlias),
            ])).Identity);
    }

    // ---- the frozen authority ---------------------------------------------------------------------

[Fact]
    public void The_successor_provider_model_input_authority_is_frozen()
    {
        var plan = Plan();
        var gold = CanonicalGoldRegistry.ResolveOccurrenceGoldAt("eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json", "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65", "DOC-0252");
        Assert.Equal(ApprovedHeadings, gold.Headings.Count);

        var representable = Representable(plan, gold, out var partCounts, out var claims);

        // The correction this whole file exists to record: request bytes now come from exactly
        // one place, and this is the proof, not an assumption. A classifier that would throw if
        // called captures what composing a request alone produces; a second, ordinary recording
        // classifier captures what a real (if transport-free) InferAsync call actually sends. If
        // request composition ever grew a second implementation again, this equality is what would
        // catch it.
        var composed = ComposedRequests(plan);
        using var recording = new RecordingClassifier();
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            recording, SemanticCoordinateContract.PdfStructuredSourceParts);
        model.InferAsync(plan.CreateProductionInput("DOC-0252"), new SemanticContextPacket([], [], []), "successor-authority-proof")
            .GetAwaiter().GetResult();

        Assert.Equal(composed.Count, recording.Requests.Count);
        Assert.Equal(composed, recording.Requests);

        var providerModelInputPlanHash = CanonicalSemanticRequestComposer.Hash(string.Join(
            "\u0000", composed.Select(CanonicalSemanticRequestComposer.Hash)));

        FreezeArtifact.AssertJson(Artifacts, "doc-0252-segment-evidence-authority.v1.json", new
        {
            artifactKind = "a99_pdf_segment_evidence_authority",
            schemaVersion = "a99-pdf-segment-evidence-authority-v1",
            capability = "SEGMENT_ATOM_SOURCE_AND_MODEL_EVIDENCE",
            authorityId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,

            active = false,
            providerAuthorized = false,
            canonicalGoldMigrated = false,
            note = "This is what a migration would switch to, measured before switching. The active lane still builds its universe from parser blocks.",

            whyFourHashes = "A universe hash says what a coordinate is. It does not say what the model is told about it, nor how that is cut into requests. All three can change while the first stays still, which is how a preflight comes back green over an input nobody checked.",

            lineage = new
            {
                note = "Where this candidate sits. Canonical Gold was frozen against the active universe, which is still what production builds; nothing here has replaced it.",
                sourceSha256 = GoldSource(),
                goldFrozenAgainstSourceUniverseSha256 = GoldUniverse(),
                activeRuntimeSourceUniverseSha256 = ActiveUniverse(),
                candidateReplacesActive = false,
            },

            hashes = new
            {
                sourceAliasUniverseSha256 = plan.SourceAliasUniverseHash,
                modelVisibleEvidenceSha256 = plan.ModelVisibleEvidenceHash,
                callPlanSha256 = plan.CallPlanHash,
                producers = new
                {
                    sourceAliasUniverse = "PdfStructuredSourceAuthorityBuilder.Build -> PdfSegmentAtomCatalog.FromSegments, schema a99-pdf-segment-atom-universe-v1",
                    modelVisibleEvidence = "PdfStructuredSourceAuthorityBuilder.Visible over PdfCanonicalSourceUniverseBuilder.EvidenceOf, schema a99-pdf-model-visible-evidence-v1",
                    callPlan = "PdfStructuredSourceAuthorityBuilder.Partition, OwnedPerSegment 120 and VisibleMargin 20, schema a99-pdf-context-pack-plan-v1",
                },
            },

            requestAuthorityCorrection = new
            {
                note = "PdfStructuredSourceAuthorityBuilder used to compute a fourth hash here, over a request format it invented for measurement. It disagreed with CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.InferAsync - same evidence, same six calls, different bytes - because it was never checked against production until a routing preflight built the real request and compared it. Discovered at PROVIDER_CALLS = 0.",
                predecessorRequestPlanSha256 = PredecessorSyntheticRequestPlanHash,
                predecessorStatus = "SYNTHETIC_REQUEST_SERIALIZATION_NOT_PROVIDER_BOUND",
                rootCause = "SCHEMA_SERIALIZED_INSIDE_JSON_IN_MEASUREMENT_HELPER_INSTEAD_OF_CANONICAL_POSTFIX_USED_BY_INFERASYNC. The predecessor also omitted the layout-block label InferAsync's own evidence shaping did not yet attach for this contract - a second, smaller finding surfaced by the same check, corrected in the same change that added CanonicalSemanticRequestComposer.",
                unchangedAuthorityDimensions = new[]
                {
                    "SOURCE_ALIAS", "MODEL_VISIBLE_EVIDENCE", "CALL_PARTITION", "GOLD", "PROMPT", "CONTRACT",
                },
                successorProviderModelInputPlanSha256 = providerModelInputPlanHash,
                successorProducer = "CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.ComposeRequests -> CanonicalSemanticRequestComposer.Compose, the same path InferAsync sends to a classifier",
                integrationProof = "recording.Requests (captured through a real InferAsync call, transport-free) equals ComposedRequests (the dry-run producer) byte for byte, both asserted above before this artifact is written.",
                predecessorNotMalicious = "A measurement artifact with narrower semantics than its name implied, not bad data: the source/evidence/partition dimensions it stood beside were, and remain, correct.",
            },

            terminology = new
            {
                blockDiagnosticSha256 = BlockDiagnosticHash,
                blockDiagnosticMeaning = "467 coordinate units: ContinuationV2 blocks over V3 segment lines. Named lineSegmentShadowSha256 in the step that produced it, which is what sent the previous migration at the wrong target. It is a diagnostic, never an atom universe.",
                candidateSegmentSha256 = CandidateSegmentHash,
                candidateSegmentMeaning = "The 650 atoms serialized through the generic alias catalog, which renumbers them S0001..S0650.",
                candidateHashMatch = plan.SourceAliasUniverseHash == CandidateSegmentHash,
                whyTheCandidateDiffers = "The rows are the same and the addressing is not. The atoms are named L{row}:S{segment}, which is what the binder resolved and what the validated S0616 proof cites; the candidate renamed them to running numbers. Only the sourceAlias field differs.",
                retiredAliasSample = new[] { "S0001", "S0002", "S0003" },
                atomAliasSample = plan.Atoms.Take(3).Select(atom => atom.Alias).ToArray(),
                retiredSchemeStatus = "The builder that could produce it has been removed. The comparison it supported was made once, recorded here, and is not re-derived: a seam kept only to demonstrate a naming difference is a second address for the same coordinates waiting to be used.",
                retiredSchemeSameRowsAndText = true,
            },

            counts = new
            {
                segmentAtoms = plan.Atoms.Count,
                modelVisibleEvidenceUnits = plan.Evidence.Count,
                atomsWithEvidence = plan.Evidence.Count,
                atomsWithLayoutBlockLabel = plan.Atoms.Count(atom => plan.LayoutBlockByAtom.ContainsKey(atom.SourceId)),
                emptyEvidenceFromIdMismatch = plan.Atoms.Count(atom => !plan.LayoutBlockByAtom.ContainsKey(atom.SourceId)),
                distinctLayoutBlocks = plan.LayoutBlockByAtom.Values.Distinct(StringComparer.Ordinal).Count(),
                contextPacks = plan.Packs.Count,
                primaryCallsPerRepeat = plan.Packs.Count,
                totalPrimaryCallsForThreeRepeats = plan.Packs.Count * 3,
            },

            packs = plan.Packs.Select((pack, index) => new
            {
                pack.Index,
                owned = pack.OwnedAliases.Count,
                visible = pack.VisibleAliases.Count,
                firstOwned = pack.OwnedAliases[0],
                lastOwned = pack.OwnedAliases[^1],
                requestSha256 = CanonicalSemanticRequestComposer.Hash(composed[index]),
                requestChars = composed[index].Length,
            }).ToArray(),

            contract = new
            {
                activeSemanticContractSha256 = "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e",
                shadowStructuredContractSha256 = SemanticSourcePartsContract.SchemaHash(),
                requestsCarryShadowSchema = true,
                activeContractSwitched = false,
                promptTemplateChanged = false,
                promptTemplateNote = "The active DOCX/legacy-PDF prompt template is untouched here. This artifact predates the structured PDF prompt clause, which the coordinate contract now supplies separately (SemanticCoordinateContract.PdfStructuredSourceParts.PromptClause); that activation is recorded where it happened, not restated here.",
            },

            representability = new
            {
                approvedHeadings = ApprovedHeadings,
                sourceHeadingsRepresentable = representable,
                headings1Part = partCounts.Count(count => count == 1),
                headings2Parts = partCounts.Count(count => count == 2),
                headings3PlusParts = partCounts.Count(count => count >= 3),
                note = "Measured over the texts canonical Gold records today. Sixteen of them still carry punctuation the old line reconstruction dropped; that debt was migrated in DOC-0252's structured Gold, not here.",
                claims,
            },

            s0616 = Wrapped(plan),
        });

        Assert.Equal(ApprovedHeadings, representable);
        Assert.Equal(Atoms, plan.Atoms.Count);
        Assert.NotEqual(BlockDiagnosticHash, plan.SourceAliasUniverseHash);
        Assert.NotEqual(PredecessorSyntheticRequestPlanHash, providerModelInputPlanHash);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>The source universe canonical Gold was frozen against, read from Gold itself.</summary>
    private static string GoldUniverse()
    {
        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        return gold.RootElement.GetProperty("occurrence").GetProperty("sourceUniverseSha256").GetString()!;
    }

    private static string GoldSource()
    {
        using var document = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        return document.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString()!;
    }

    /// <summary>What production builds today, recomputed rather than quoted.</summary>
    private static string ActiveUniverse()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        var lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.MidpointV1);
        return PdfCanonicalSourceUniverseBuilder.Build(Doc0252Path, lines).SourceUniverseSha256;
    }

    private static string Doc0252Path => System.IO.Path.Combine(
        TestRepository.Root(), Doc0252.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<PdfLine> Segments()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        return PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3);
    }

    private static PdfStructuredSourceAuthority Plan() =>
        PdfStructuredSourceAuthorityBuilder.Build(Segments(), sourceSha256: Doc0252SourceSha256);

    /// <summary>
    /// Every request the plan would actually produce, through the one real composer - the same
    /// path <see cref="CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.InferAsync"/>
    /// sends to a classifier, reached here without one.
    /// </summary>
    private static IReadOnlyList<string> ComposedRequests(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(), SemanticCoordinateContract.PdfStructuredSourceParts);
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .Select(segment => segment.RequestBytes)
            .ToArray();
    }

    /// <summary>A classifier that must never be called - proof that composing requests transports nothing.</summary>
    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException("composing a request must not need a model name");
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0: composing a request must never transport.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// Captures the exact bytes InferAsync sends, without transporting them anywhere - the no-network
    /// proof that <see cref="CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.InferAsync"/>
    /// and its own dry-run <c>ComposeRequests</c> genuinely produce the same bytes, not just similar
    /// ones.
    /// </summary>
    private sealed class RecordingClassifier : IHeaderClassifier
    {
        public List<string> Requests { get; } = [];
        public string ModelName => "recording";
        public int ContextSize => 8192;
        public string RuntimeDescription => "recording";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Requests.Add(userMessage);
            return Task.FromResult("{\"headings\":[]}");
        }
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static object Wrapped(PdfStructuredSourceAuthority plan)
    {
        var first = plan.Atoms.Single(atom => atom.Alias == "L0359:S0");
        var second = plan.Atoms.Single(atom => atom.Alias == "L0360:S0");
        var bound = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(
        [
            new SemanticSourcePart(first.Alias, CanonicalSemanticSelectionMode.WholeAlias),
            new SemanticSourcePart(second.Alias, CanonicalSemanticSelectionMode.WholeAlias),
        ]));

        return new
        {
            note = "The heading that no single occurrence could hold under block authority. Both atoms reach the model and the binder resolves them; the layout blocks they sit in differ and are not consulted.",
            atom1 = new { first.Alias, first.Text, pack = PackOf(plan, first.Alias), layoutBlock = plan.LayoutBlockByAtom[first.SourceId] },
            atom2 = new { second.Alias, second.Text, pack = PackOf(plan, second.Alias), layoutBlock = plan.LayoutBlockByAtom[second.SourceId] },
            sameLayoutBlockUnderCandidateGrouping = plan.LayoutBlockByAtom[first.SourceId] == plan.LayoutBlockByAtom[second.SourceId],
            bindingIsTheSameUnderEitherGrouping = true,
            bothVisibleToTheModel = true,
            blockRequiredForBinding = false,
            bound = bound.IsBound,
            identity = bound.Identity,
            locality = bound.Parts.Skip(1).Select(part => part.LocalityFromPrevious.ToString()).ToArray(),
            projectedText = SemanticSourceProjection.Render(bound.Parts),
        };
    }

    private static int PackOf(PdfStructuredSourceAuthority plan, string alias) =>
        plan.Packs.First(pack => pack.OwnedAliases.Contains(alias)).Index;

    /// <summary>
    /// How many approved headings the atom universe can express, using the validated binder and
    /// the texts Gold records. The locating is the same one the structured-binding audit used.
    /// </summary>
    private static int Representable(
        PdfStructuredSourceAuthority plan, PdfGoldDocument gold, out List<int> partCounts, out object[] claims)
    {
        var reference = ReferenceOccurrences();
        var aliases = PdfSourceOccurrenceBoundary.Aliases(reference.Count);
        var rows = new List<object>();
        partCounts = [];
        var bound = 0;
        var cursor = 0;

        for (var ordinal = 0; ordinal < gold.Headings.Count; ordinal++)
        {
            var heading = gold.Headings[ordinal];
            var text = heading.VerbatimText ?? reference[Array.IndexOf(aliases, heading.SourceAlias)].VerbatimText;
            var parts = StructuredSourcePartLocator.Locate(plan.Atoms, text, punctuationInsensitive: true, ref cursor);
            var binding = parts is null
                ? new SemanticSourcePartsBinding(SemanticSourcePartsStatus.TextNotInAtom, [], "no atom run holds this heading")
                : SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(parts));

            if (binding.IsBound)
            {
                bound++;
                partCounts.Add(binding.Parts.Count);
            }

            rows.Add(new
            {
                claim = $"{heading.SourceAlias}#{ordinal}",
                representable = binding.IsBound,
                status = binding.Status.ToString(),
                parts = binding.Parts.Count,
                identity = binding.Identity,
                packs = binding.Parts.Select(part => PackOf(plan, part.Alias)).Distinct().ToArray(),
            });
        }

        claims = [.. rows];
        return bound;
    }

    private static IReadOnlyList<PdfSemanticBlock> ReferenceOccurrences()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        var lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.MidpointV1);
        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }
}
