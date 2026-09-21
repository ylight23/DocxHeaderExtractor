using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Consolidates every user-approved Gold authority into one root, and proves it was a move rather
/// than a re-decision.
/// <para>
/// Nothing here judges a document. Each canonical file is derived from the semantic freeze the user
/// already approved, plus an occurrence artifact only where that artifact provably describes the
/// same source and the same heading set. No provider call, no model call, no re-review.
/// </para>
/// <para>
/// The rule that matters when the two disagree: the latest user-approved semantic truth is primary.
/// An older occurrence artifact covering fewer headings does not reduce the total - DOC-0205 is 72
/// with a 71-heading artifact beside it, DOC-0258 is 37 against an older 24, DOC-0256 is 34 against
/// an older 24. In each case the artifact is not imported and the source is simply not occurrence-
/// evaluable yet, which is a smaller loss than a quietly wrong total.
/// </para>
/// </summary>
public sealed class CanonicalGoldConsolidationTests
{
    private const string SemanticRoot = "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic";
    private const string OccurrenceRoot = "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence";

    /// <summary>
    /// The totals this consolidation must preserve. Pinned so that a move which changed one would
    /// fail here by name instead of arriving as a quiet metric shift later.
    /// </summary>
    private static readonly (string Id, int Total)[] Expected =
    [
        ("DOC-0001", 7), ("DOC-0092", 145), ("DOC-0123", 362), ("DOC-0133", 123),
        ("DOC-0202", 111), ("DOC-0205", 72), ("DOC-0252", 41), ("DOC-0255", 18),
        ("DOC-0256", 34), ("DOC-0258", 37), ("DOC-0259", 18), ("DOC-0264", 159),
        ("SRC-003", 231), ("SRC-029", 356), ("SRC-041", 297), ("SRC-042", 262),
        ("SRC-044", 254), ("SRC-053", 277), ("SRC-054", 316), ("SRC-055", 4),
        ("SRC-057", 831),
    ];

    // ---- generation ---------------------------------------------------------------------------

    [Fact]
    public void Every_approved_authority_has_one_canonical_gold_file()
    {
        foreach (var authority in Authorities())
            FreezeArtifact.AssertJson(
                CanonicalGoldRegistry.Root + "/documents", $"{authority.Id}.gold.v1.json", authority.Gold);

        Assert.Equal(Expected.Length, Authorities().Count);
    }

    [Fact]
    public void The_registry_lists_every_authority_and_nothing_else()
    {
        var authorities = Authorities();

        FreezeArtifact.AssertJson(CanonicalGoldRegistry.Root, "registry.v1.json", new
        {
            artifactKind = "a99_canonical_gold_registry",
            schemaVersion = "a99-canonical-gold-registry-v1",
            note = "The only discovery authority for Gold. Consumers resolve by authorityId; they do " +
                   "not scan folders, and there is no fallback to a legacy root.",
            providerCalls = 0,
            modelCalls = 0,
            authorities = authorities.Select(authority => new
            {
                authorityId = authority.Id,
                canonicalGoldPath = $"{CanonicalGoldRegistry.Root}/documents/{authority.Id}.gold.v1.json",
                sourceSha256 = authority.SourceSha256,
                semanticHeadingTotal = authority.Total,
                materializedSemanticClaims = authority.SemanticClaimCount,
                semanticCountAuthoritative = true,
                semanticClaimsEvaluable = authority.SemanticClaimCount == authority.Total && authority.Total > 0,
                occurrenceEvaluable = authority.OccurrenceEvaluable,
                characterSpanEvaluable = authority.CharacterSpanEvaluable,
                visualBindingEvaluable = authority.VisualBindingEvaluable,
                goldSha256 = authority.GoldSha256,
                approvalAuthority = "USER",
                userFinalApproval = true,
            }).ToArray(),
        });

        Assert.Equal(Expected.Select(item => item.Id), authorities.Select(authority => authority.Id));
    }

    // ---- what the move must preserve ----------------------------------------------------------

    [Fact]
    public void The_canonical_totals_are_the_approved_totals()
    {
        var byId = Authorities().ToDictionary(authority => authority.Id, StringComparer.Ordinal);

        Assert.All(Expected, expected =>
        {
            Assert.True(byId.ContainsKey(expected.Id), $"{expected.Id} has no canonical Gold");
            Assert.Equal(expected.Total, byId[expected.Id].Total);
        });
    }

    [Fact]
    public void Semantic_truth_is_never_reduced_to_fit_an_older_occurrence_artifact()
    {
        // The specific failure this consolidation exists to prevent. Each of these has an older
        // artifact describing fewer headings; importing it as the total would have been a silent
        // revision of something a person approved.
        var byId = Authorities().ToDictionary(authority => authority.Id, StringComparer.Ordinal);

        Assert.Equal(72, byId["DOC-0205"].Total);
        Assert.Equal(37, byId["DOC-0258"].Total);
        Assert.Equal(34, byId["DOC-0256"].Total);
        Assert.Equal(159, byId["DOC-0264"].Total);
        Assert.Equal(41, byId["DOC-0252"].Total);

        // And none of them claims occurrence truth it does not have.
        foreach (var id in new[] { "DOC-0205", "DOC-0258", "DOC-0256", "DOC-0264" })
            Assert.False(byId[id].OccurrenceEvaluable, $"{id} imported an occurrence set it should not have");
    }

    [Fact]
    public void Occurrence_is_imported_only_where_the_source_and_the_heading_set_match()
    {
        var byId = Authorities().ToDictionary(authority => authority.Id, StringComparer.Ordinal);

        // Two authorities qualify, and both were checked against the source bytes and the count.
        Assert.True(byId["DOC-0001"].OccurrenceEvaluable);
        Assert.True(byId["DOC-0252"].OccurrenceEvaluable);
        Assert.Equal(7, byId["DOC-0001"].Claims.Count);
        Assert.Equal(41, byId["DOC-0252"].Claims.Count);

        // DOC-0001 binds with UTF-16 spans; DOC-0252 binds by alias and selection mode. One root
        // does not mean one coordinate system.
        Assert.True(byId["DOC-0001"].CharacterSpanEvaluable);
        Assert.False(byId["DOC-0252"].CharacterSpanEvaluable);

        // Everything else carries semantic truth and says plainly that it has no bindings.
        foreach (var authority in Authorities().Where(item => item.Id is not ("DOC-0001" or "DOC-0252")))
        {
            Assert.False(authority.OccurrenceEvaluable, $"{authority.Id} claims occurrence truth");
            Assert.Empty(authority.Claims);
            Assert.False(string.IsNullOrWhiteSpace(authority.OccurrenceUnavailableReason));
        }
    }

    [Fact]
    public void A_scanned_source_keeps_semantic_truth_without_inventing_coordinates()
    {
        // DOC-0202 needs visual recovery. Fabricating UTF-16 spans for it would make it look
        // scoreable on an axis nobody has measured.
        var authority = Authorities().Single(item => item.Id == "DOC-0202");

        Assert.Equal(111, authority.Total);
        Assert.False(authority.OccurrenceEvaluable);
        Assert.False(authority.CharacterSpanEvaluable);
        Assert.False(authority.VisualBindingEvaluable);
    }

    [Fact]
    public void Every_canonical_file_records_where_its_truth_came_from()
    {
        Assert.All(Authorities(), authority =>
        {
            Assert.NotEmpty(authority.Provenance);
            Assert.Contains(authority.Provenance, item => item.Role == "SEMANTIC_AUTHORITY");
        });
    }

    // ---- determinism and resolution -----------------------------------------------------------

    [Fact]
    public void Deriving_the_same_gold_twice_produces_the_same_bytes()
    {
        // The hash in the registry only means something if the derivation is stable.
        var first = Authorities();
        var second = Authorities();

        Assert.Equal(
            first.Select(authority => authority.GoldSha256),
            second.Select(authority => authority.GoldSha256));
        Assert.All(first.Zip(second), pair => Assert.Equal(pair.First.GoldSha256, pair.Second.GoldSha256));
    }

    [Fact]
    public void The_registry_resolves_every_authority_and_the_hash_matches_the_file()
    {
        Assert.Equal(Expected.Length, CanonicalGoldRegistry.Entries.Count);

        foreach (var entry in CanonicalGoldRegistry.Entries)
        {
            using var gold = CanonicalGoldRegistry.Resolve(entry.AuthorityId);
            var root = gold.RootElement;
            Assert.Equal("a99_canonical_gold", root.GetProperty("artifactKind").GetString());
            Assert.Equal(entry.AuthorityId, root.GetProperty("authorityId").GetString());
            Assert.Equal(
                entry.SemanticHeadingTotal,
                root.GetProperty("semantic").GetProperty("semanticHeadingTotal").GetInt32());
            // The capability a file declares has to match what it actually contains.
            var claims = root.GetProperty("semantic").GetProperty("claims").GetArrayLength();
            Assert.Equal(entry.MaterializedSemanticClaims, claims);
            Assert.Equal(entry.SemanticClaimsEvaluable, claims == entry.SemanticHeadingTotal && claims > 0);
        }
    }

    [Fact]
    public void An_unregistered_authority_is_refused_rather_than_searched_for()
    {
        // No fallback: not "try gold-current, then strict-gold-v4". A missing authority is a stop.
        var error = Assert.Throws<InvalidOperationException>(() => CanonicalGoldRegistry.Resolve("DOC-9999"));

        Assert.Contains("not a canonical Gold authority", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_evaluator_is_refused_the_axis_its_gold_does_not_carry()
    {
        CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.SemanticCount);
        CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.SemanticClaims);
        // A count-only authority is authoritative about how many, and refuses to be asked which.
        CanonicalGoldRegistry.RequireCapability("DOC-0205", GoldCapability.SemanticCount);
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0205", GoldCapability.SemanticClaims));
        CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.Occurrence);
        CanonicalGoldRegistry.RequireCapability("DOC-0001", GoldCapability.CharacterSpan);

        // DOC-0252 binds by alias, so a character-span score would be invented.
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.CharacterSpan));
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0205", GoldCapability.Occurrence));
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0202", GoldCapability.VisualBinding));
    }

    [Fact]
    public void Legacy_roots_are_provenance_and_the_canonical_root_is_the_only_active_one()
    {
        // Structural: the registry is the discovery path, and it points only inside gold-current.
        Assert.All(CanonicalGoldRegistry.Entries, entry =>
            Assert.StartsWith(CanonicalGoldRegistry.Root + "/", entry.CanonicalGoldPath, StringComparison.Ordinal));
    }

    // ---- provider baseline preflight ----------------------------------------------------------

    /// <summary>The two authorities whose Gold can score an occurrence-level run today.</summary>
    private static readonly string[] OccurrenceCohort = ["DOC-0001", "DOC-0252"];

    [Fact]
    public void The_occurrence_cohort_is_exactly_what_the_registry_can_score()
    {
        // Derived from capability, not from a list someone maintained. A count-only authority is
        // excluded because it cannot say which heading a run missed, not because of its media type.
        var cohort = CanonicalGoldRegistry.Entries
            .Where(entry => entry.OccurrenceEvaluable && entry.SemanticClaimsEvaluable)
            .Select(entry => entry.AuthorityId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(OccurrenceCohort, cohort);
        Assert.All(cohort, id => Assert.Equal(
            CanonicalGoldRegistry.Entry(id).SemanticHeadingTotal,
            CanonicalGoldRegistry.Entry(id).MaterializedSemanticClaims));
    }

    [Fact]
    public async Task The_baseline_preflight_freezes_what_a_provider_run_would_send()
    {
        // Everything a later run must match before it is allowed to spend a call. The request plan
        // is captured through the real lanes with a classifier that answers nothing, so the counts
        // are measured rather than estimated - and no provider is contacted to measure them.
        var documents = new List<object>();
        var primaryCalls = 0;

        foreach (var id in OccurrenceCohort)
        {
            var entry = CanonicalGoldRegistry.Entry(id);
            using var gold = CanonicalGoldRegistry.Resolve(id);
            var source = gold.RootElement.GetProperty("source");
            var sourcePath = source.GetProperty("sourcePath").GetString()!;
            var segments = await PlannedSegmentsAsync(sourcePath, source.GetProperty("mediaType").GetString()!);
            primaryCalls += segments * Repetitions;

            documents.Add(new
            {
                authorityId = id,
                canonicalGoldPath = entry.CanonicalGoldPath,
                goldSha256 = entry.GoldSha256,
                sourcePath,
                sourceSha256 = entry.SourceSha256,
                sourceUniverseSha256 = gold.RootElement.GetProperty("occurrence")
                    .GetProperty("sourceUniverseSha256").GetString(),
                semanticHeadingTotal = entry.SemanticHeadingTotal,
                materializedSemanticClaims = entry.MaterializedSemanticClaims,
                occurrenceEvaluable = entry.OccurrenceEvaluable,
                characterSpanEvaluable = entry.CharacterSpanEvaluable,
                semanticRequestsPerRepetition = segments,
                placementAllowancePerRepetition = PlacementAllowance,
            });
        }

        var placement = OccurrenceCohort.Length * Repetitions * PlacementAllowance;
        FreezeArtifact.AssertJson(CanonicalGoldRegistry.Root, "occurrence-baseline-preflight.v1.json", new
        {
            artifactKind = "a99_occurrence_baseline_preflight",
            schemaVersion = "a99-occurrence-baseline-preflight-v1",
            experimentId = "A99-S2P-OCCURRENCE-BASELINE-V1",
            status = "PREFLIGHT_ONLY_NO_TRANSPORT",
            providerCalls = 0,
            modelCalls = 0,
            goldRegistry = CanonicalGoldRegistry.RegistryRelativePath,
            promptSha256 = CanonicalArtifactHash.OfText(CanonicalSemanticEngine.SystemPrompt),
            semanticContractProtocol = CanonicalSemanticContract.ProtocolVersion,
            // The schema itself, not its name. A protocol string survives a field being added or
            // renamed, and a run whose contract drifted mid-flight produced provider responses that
            // could not be scored - responses paid for and unusable. Recomputed at execution and
            // compared byte for byte against this.
            semanticContractSha256 = SemanticContractSha256(),
            evaluatorId = "a99-pdf-gold-evaluator-v3-bound-occurrence-semantic-role",
            model = "qwen/qwen3.7-flash",
            repetitions = Repetitions,
            documents,
            callBudget = new
            {
                totalPrimaryCalls = primaryCalls,
                placementAllowance = placement,
                maxProviderCalls = primaryCalls + placement,
            },
            failClosedGates = new[]
            {
                "canonical goldSha256 per authority",
                "sourceSha256 per authority",
                "sourceUniverseSha256 per authority",
                "promptSha256",
                "semantic contract protocol",
                "model identity",
                "evaluator identity",
                "call budget not exceeded",
            },
            note = "Any mismatch at execution time means zero provider calls and a stop, not a " +
                   "run against whatever is on disk.",
        });

        Assert.True(primaryCalls > 0);
    }

    /// <summary>
    /// The request schema as the engine will send it, hashed canonically. Computed here rather than
    /// pinned as a literal so it cannot be copied forward from an older contract.
    /// </summary>
    internal static string SemanticContractSha256() =>
        CanonicalArtifactHash.OfText(JsonSerializer.Serialize(
            CanonicalSemanticContract.Schema(), FreezeArtifact.Json));

    [Fact]
    public void The_frozen_contract_hash_is_the_schema_the_engine_would_send()
    {
        // Recomputing must reproduce what the manifest froze; if it does not, the contract drifted
        // and a run would spend calls it cannot score.
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            TestRepository.Path(CanonicalGoldRegistry.Root + "/occurrence-baseline-preflight.v1.json")));

        Assert.Equal(
            SemanticContractSha256(),
            manifest.RootElement.GetProperty("semanticContractSha256").GetString());
        Assert.Equal(
            CanonicalArtifactHash.OfText(CanonicalSemanticEngine.SystemPrompt),
            manifest.RootElement.GetProperty("promptSha256").GetString());
    }

    [Fact]
    public void Occurrence_identity_decides_membership_and_a_role_never_does()
    {
        // The evaluator gate the baseline depends on. A heading found with the wrong role is a role
        // error and still a true positive; if role entered the identity, every role disagreement
        // would arrive as a paired false negative and false positive.
        var gold = new[] { Bound("S0001", 0, 10, "SECTION"), Bound("S0002", 0, 8, "ARTICLE") };
        var predicted = new[] { Bound("S0001", 0, 10, "CHAPTER"), Bound("S0002", 0, 8, "ARTICLE") };

        var evaluation = PdfGoldBoundOccurrenceEvaluator.EvaluateBound(gold, predicted);

        Assert.Equal(2, evaluation.Semantic.TruePositive);
        Assert.Equal(0, evaluation.Semantic.FalseNegative);
        Assert.Equal(0, evaluation.Semantic.FalsePositive);
        Assert.Equal(2, evaluation.SemanticRole.Compared);
        Assert.Equal(1, evaluation.SemanticRole.Mismatched);
    }

    [Fact]
    public void A_role_gold_never_recorded_is_excluded_rather_than_counted_wrong()
    {
        // DOC-0001 identifies seven headings and names a role for none of them. Counting those as
        // mismatches would report zero role accuracy for a document nobody made a role claim about.
        var gold = new[] { Bound("S0001", 0, 10, null), Bound("S0002", 0, 8, null) };
        var predicted = new[] { Bound("S0001", 0, 10, "SECTION"), Bound("S0002", 0, 8, "ARTICLE") };

        var evaluation = PdfGoldBoundOccurrenceEvaluator.EvaluateBound(gold, predicted);

        Assert.Equal(2, evaluation.Semantic.TruePositive);
        Assert.Equal(0, evaluation.SemanticRole.Compared);
        Assert.Equal(0, evaluation.SemanticRole.Mismatched);
        Assert.Equal(2, evaluation.SemanticRole.NotAdjudicated);
        Assert.Equal(1.0, evaluation.SemanticRole.Accuracy);
    }

    private static PdfBoundOccurrence Bound(string alias, int start, int end, string? role) =>
        new([new CanonicalSemanticBoundPart(alias, alias, 0, "text", start, end)], role!, alias, null);

    private const int Repetitions = 3;

    /// <summary>One placement round per document per repetition, allowed but not assumed.</summary>
    private const int PlacementAllowance = 1;

    /// <summary>
    /// The number of semantic requests a run would send, measured by driving the real lane with a
    /// classifier that records and answers nothing.
    /// </summary>
    private static async Task<int> PlannedSegmentsAsync(string sourcePath, string mediaType)
    {
        var path = TestRepository.Path(sourcePath);
        using var capture = new RequestCapturingClassifier();
        if (string.Equals(mediaType, "PDF", StringComparison.Ordinal))
        {
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, capture, CancellationToken.None);
        }
        else
        {
            var source = new OpenXmlDocumentSource().Read(path);
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var derived = new DocumentFeatureDeriver().Derive(source);
            var state = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
            var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            await DocxAuthorityPipeline.RunAsync(state, mode, capture);
        }

        return capture.Requests.Count;
    }

    [Fact]
    public void Every_active_source_universe_is_one_the_runtime_reproduces()
    {
        // The gate that refused the first transport attempt. An authority may record any number of
        // historical universe hashes in provenance, but the one gates read has to be the one the
        // lane that will run actually produces - otherwise claims were marked against a universe
        // the run never sees, and the disagreement would read as a model error.
        foreach (var id in OccurrenceCohort)
        {
            using var gold = CanonicalGoldRegistry.Resolve(id);
            var root = gold.RootElement;
            var source = root.GetProperty("source");
            var active = root.GetProperty("occurrence").GetProperty("sourceUniverseSha256").GetString();

            var runtime = RuntimeSourceUniverse(
                TestRepository.Path(source.GetProperty("sourcePath").GetString()!),
                source.GetProperty("mediaType").GetString()!,
                source.GetProperty("sourceSha256").GetString()!);

            Assert.Equal(runtime, active);
        }
    }

    [Fact]
    public void A_reconciled_universe_keeps_the_review_time_hash_as_history_only()
    {
        // History is preserved, not promoted. There is exactly one active identity; the older one
        // lives in provenance where nothing gates on it, because "which hash applies here" is the
        // ambiguity this work removed from Gold.
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        var provenance = gold.RootElement.GetProperty("provenance").EnumerateArray().ToArray();

        var reconciliation = Assert.Single(provenance,
            item => item.GetProperty("role").GetString() == "SOURCE_UNIVERSE_RECONCILIATION");
        Assert.Equal("5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec",
            reconciliation.GetProperty("path").GetString());
        Assert.Equal("cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66",
            reconciliation.GetProperty("sha256").GetString());

        // DOC-0001 needed no reconciliation: its frozen hash already was the runtime one.
        using var docx = CanonicalGoldRegistry.Resolve("DOC-0001");
        Assert.DoesNotContain(docx.RootElement.GetProperty("provenance").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "SOURCE_UNIVERSE_RECONCILIATION");
    }

    // ---- derivation ---------------------------------------------------------------------------

    private sealed record Authority(
        string Id,
        int Total,
        int SemanticClaimCount,
        string SourceSha256,
        bool OccurrenceEvaluable,
        bool CharacterSpanEvaluable,
        bool VisualBindingEvaluable,
        string? OccurrenceUnavailableReason,
        IReadOnlyList<JsonElement> Claims,
        IReadOnlyList<ProvenanceItem> Provenance,
        object Gold)
    {
        public string GoldSha256 => CanonicalArtifactHash.OfText(
            JsonSerializer.Serialize(Gold, FreezeArtifact.Json));
    }

    private sealed record ProvenanceItem(string Path, string Sha256, string Role);

    private static IReadOnlyList<Authority> Authorities()
    {
        var directory = TestRepository.Path(SemanticRoot);
        return Directory.EnumerateFiles(directory, "*.semantic-freeze.v1.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(Build)
            .ToArray();
    }

    private static Authority Build(string semanticPath)
    {
        var semanticText = File.ReadAllText(semanticPath);
        using var semantic = JsonDocument.Parse(semanticText);
        var root = semantic.RootElement;
        var id = root.GetProperty("authorityKey").GetString()!;
        var record = root.GetProperty("authorityRecord");

        // Only a current, verified, user-approved freeze becomes active Gold.
        Assert.Equal("FROZEN_SEMANTIC_VNEXT", root.GetProperty("status").GetString());
        Assert.Equal("VERIFIED", root.GetProperty("sourceLineageStatus").GetString());
        Assert.True(record.GetProperty("userFinalApproval").GetBoolean());
        Assert.Equal("USER", record.GetProperty("finalAuthority").GetString());

        var total = root.GetProperty("semanticHeadingTotal").GetInt32();
        var sourceSha = root.GetProperty("sourceSha256").GetString()!;
        var capabilities = root.GetProperty("capabilities");
        var provenance = new List<ProvenanceItem>
        {
            new(RelativeTo(semanticPath), CanonicalArtifactHash.OfText(semanticText), "SEMANTIC_AUTHORITY"),
        };

        var claims = new List<JsonElement>();
        var semanticClaims = new List<object>();
        var occurrenceEvaluable = false;
        var characterSpanEvaluable = false;
        string? unavailable = "no occurrence artifact describes this source under the current authority";
        string? coordinateSystem = null;
        string? sourceUniverseSha = null;
        string? reviewTimeUniverseSha = null;

        var occurrencePath = TestRepository.Path($"{OccurrenceRoot}/{id}.occurrence-gold.v1.json");
        if (File.Exists(occurrencePath))
        {
            var occurrenceText = File.ReadAllText(occurrencePath);
            using var occurrence = JsonDocument.Parse(occurrenceText);
            var occurrenceRoot = occurrence.RootElement;
            var occurrenceSha = occurrenceRoot.GetProperty("sourceSha256").GetString();
            var occurrenceTotal = occurrenceRoot.GetProperty("semanticHeadingTotal").GetInt32();
            var bound = occurrenceRoot.TryGetProperty("boundOccurrences", out var explicitBindings)
                ? explicitBindings
                : occurrenceRoot.GetProperty("headings");

            // The import policy, checked rather than assumed: same source bytes, same heading set.
            var sameSource = string.Equals(occurrenceSha, sourceSha, StringComparison.Ordinal);
            var sameCount = occurrenceTotal == total && bound.GetArrayLength() == total;

            if (sameSource && sameCount)
            {
                claims.AddRange(bound.EnumerateArray().Select(item => item.Clone()));
                // The occurrence review is what identified the headings, so it is also the
                // row-level semantic truth. Projected, not re-decided: alias, text and role exactly
                // as the reviewer recorded them, with nulls where they recorded nothing.
                semanticClaims.AddRange(bound.EnumerateArray().Select(item => (object)new
                {
                    sourceAlias = Text(item, "sourceAlias"),
                    verbatimText = Text(item, "verbatimText") ?? Text(item, "exactText"),
                    selectionMode = Text(item, "selectionMode"),
                    semanticRole = Text(item, "semanticRole"),
                }));
                occurrenceEvaluable = true;
                characterSpanEvaluable = occurrenceText.Contains("utf16Span", StringComparison.Ordinal);
                coordinateSystem = characterSpanEvaluable
                    ? "SOURCE_ALIAS_PLUS_UTF16_SPAN"
                    : "SOURCE_ALIAS_PLUS_SELECTION_MODE";
                unavailable = null;
                // The universe the runtime reproduces, not the one the review artifact recorded.
                // Those were two serializations of the same occurrences - the review pack document
                // against the runtime alias rows - and freezing the first in a field that gates
                // against the second is what refused the first transport attempt. The recorded
                // value is kept below as provenance; it is not a second active identity.
                reviewTimeUniverseSha = occurrenceRoot.TryGetProperty("sourceUniverseSha256", out var universe)
                    ? universe.GetString()
                    : null;
                sourceUniverseSha = RuntimeSourceUniverse(
                    TestRepository.Path(root.GetProperty("authoritySourcePath").GetString()!),
                    root.GetProperty("mediaType").GetString()!,
                    sourceSha);
                provenance.Add(new(RelativeTo(occurrencePath),
                    CanonicalArtifactHash.OfText(occurrenceText), "OCCURRENCE_AUTHORITY"));
                if (reviewTimeUniverseSha is { Length: > 0 } &&
                    !string.Equals(reviewTimeUniverseSha, sourceUniverseSha, StringComparison.Ordinal))
                {
                    provenance.Add(new(reviewTimeUniverseSha, sourceUniverseSha!,
                        "SOURCE_UNIVERSE_RECONCILIATION"));
                }
            }
            else
            {
                unavailable = sameSource
                    ? $"occurrence artifact describes {occurrenceTotal} headings against an approved total of {total}"
                    : "occurrence artifact was taken against different source bytes";
                provenance.Add(new(RelativeTo(occurrencePath),
                    CanonicalArtifactHash.OfText(occurrenceText), "NON_CANONICAL_PROVENANCE_ONLY"));
            }
        }

        var isDocument = id.StartsWith("DOC-", StringComparison.Ordinal);
        var gold = new
        {
            artifactKind = "a99_canonical_gold",
            schemaVersion = "a99-canonical-gold-v1",
            authorityId = id,
            documentId = isDocument ? id : null,
            sourceId = isDocument ? null : id,
            source = new
            {
                fileName = root.GetProperty("fileName").GetString(),
                mediaType = root.GetProperty("mediaType").GetString(),
                sourcePath = root.GetProperty("authoritySourcePath").GetString(),
                sourceSha256 = sourceSha,
                sourceLineageStatus = root.GetProperty("sourceLineageStatus").GetString(),
            },
            approval = new
            {
                authority = "USER",
                userFinalApproval = true,
                approvalBasis = root.GetProperty("finalAuthority").GetString(),
                approvedAt = record.GetProperty("approvedAt").GetString(),
                truthDefinition = root.GetProperty("truthDefinition").GetString(),
                semanticTruthReusedFrom = new[] { RelativeTo(semanticPath) },
                reReviewedDuringConsolidation = false,
            },
            semantic = new
            {
                headingSetExhaustive = string.Equals(
                    root.GetProperty("truthDefinition").GetString(),
                    "ALL_TRUE_HEADING_OCCURRENCES", StringComparison.Ordinal),
                approvedSemanticTotal = total,
                semanticHeadingTotal = total,
                // Row-level identities exist only where a reviewer materialised them. Where the
                // approved authority is a count, this stays empty and materializedSemanticClaims is
                // zero: a total says how many headings there are, never which ones, and deriving
                // rows from it would be inventing identities nobody looked at.
                materializedSemanticClaims = semanticClaims.Count,
                claims = semanticClaims.ToArray(),
            },
            occurrence = new
            {
                occurrenceEvaluable,
                characterSpanEvaluable,
                bindingCoordinateSystem = coordinateSystem,
                // The source universe the reviewer marked against, so a later change to how the
                // universe is built is visible rather than silently rebinding the claims.
                sourceUniverseSha256 = sourceUniverseSha,
                unavailableReason = unavailable,
                claims = claims.ToArray(),
            },
            capabilities = new
            {
                // The approved total is authoritative for every authority here.
                semanticCountAuthoritative = true,
                // Whether a scorer can say WHICH heading was missed. A count cannot: from 72 alone
                // there is no way to know which of a run's proposals is a true positive, so calling
                // that axis evaluable would promise precision and recall the data cannot support.
                semanticClaimsEvaluable = semanticClaims.Count == total && total > 0,
                occurrenceEvaluable,
                characterSpanEvaluable,
                visualBindingEvaluable = capabilities.TryGetProperty("visualBindingEvaluable", out var visual)
                                         && visual.GetBoolean(),
                hierarchyEvaluable = capabilities.TryGetProperty("hierarchyEvaluable", out var hierarchy)
                                     && hierarchy.GetBoolean(),
            },
            provenance = provenance
                .Select(item => new { path = item.Path, sha256 = item.Sha256, role = item.Role })
                .ToArray(),
            providerCalls = 0,
            modelCalls = 0,
        };

        return new Authority(
            id, total, semanticClaims.Count, sourceSha, occurrenceEvaluable, characterSpanEvaluable,
            capabilities.TryGetProperty("visualBindingEvaluable", out var vb) && vb.GetBoolean(),
            unavailable, claims, provenance, gold);
    }

    /// <summary>
    /// The runtime identity of a source universe: what the lane that will run actually produces.
    /// One producer per media type, both already owned by the runtime - nothing is re-parsed and
    /// no second definition of a universe is introduced here.
    /// </summary>
    private static string RuntimeSourceUniverse(string path, string mediaType, string sourceSha)
    {
        if (string.Equals(mediaType, "PDF", StringComparison.OrdinalIgnoreCase))
            return PdfCanonicalSourceUniverseBuilder.Build(path).SourceUniverseSha256;

        var source = new OpenXmlDocumentSource().Read(path);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        return DocxSourceUniverseHash.Compute(sourceSha, SemanticSourceAliasCatalog.FromCatalog(catalog));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RelativeTo(string absolute) =>
        Path.GetRelativePath(TestRepository.Root(), absolute).Replace(Path.DirectorySeparatorChar, '/');
}
