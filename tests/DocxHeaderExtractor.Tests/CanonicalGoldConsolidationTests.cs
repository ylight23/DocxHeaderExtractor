using DocxHeaderExtractor.DocumentProcessing.Source;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Generates the consumer view of Gold - <c>gold-current/registry.v1.json</c> and
/// <c>gold-current/documents/{ID}.gold.v1.json</c> - from the one authored file per document in
/// <c>eval/a99-closed-loop/gold/</c> (see <see cref="GoldAuthoredSourceTests"/>), and nothing else.
/// <para>
/// Nothing here judges a document. Every decision is copied from the authored file; only what
/// follows from it - claim projections, capabilities, the runtime source universe - is derived. No
/// provider call, no model call, no re-review.
/// </para>
/// <para>
/// The authored files were migrated on 2026-09-24 from the older two-input layout (a semantic freeze
/// plus an occurrence artifact under canonical-semantic-gold-vnext); regenerating from them reproduced
/// every consumer file byte for byte. The rule that governed that layout still holds: the approved
/// semantic total is primary, and an itemised list that does not add up to it fails closed.
/// </para>
/// </summary>
public sealed class CanonicalGoldConsolidationTests
{

    /// <summary>
    /// The totals this consolidation must preserve. Pinned so that a move which changed one would
    /// fail here by name instead of arriving as a quiet metric shift later.
    /// </summary>
    private static readonly (string Id, int Total)[] Expected =
    [
        ("DOC-0001", 7), ("DOC-0092", 145), ("DOC-0123", 359), ("DOC-0133", 112),
        ("DOC-0202", 111), ("DOC-0205", 72), ("DOC-0252", 42), ("DOC-0255", 20),
        ("DOC-0256", 34), ("DOC-0258", 37), ("DOC-0259", 20), ("DOC-0264", 159),
        ("SRC-003", 231), ("SRC-029", 374), ("SRC-041", 280), ("SRC-042", 253),
        ("SRC-044", 203), ("SRC-053", 271), ("SRC-054", 298), ("SRC-055", 4),
        ("SRC-057", 831), ("SRC-089", 36), ("SRC-095", 103),
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
        Assert.Equal(42, byId["DOC-0252"].Total);

        // DOC-0205, DOC-0256 and DOC-0258 are the exceptions: their occurrence sets were migrated deliberately
        // (see Occurrence_is_imported_only_where_the_source_and_the_heading_set_match), each reaching
        // exactly the total the semantic authority already recorded - never the older artifact's
        // smaller count. The rest still claim nothing they do not have.
        foreach (var id in new[] { "DOC-0264" })
            Assert.False(byId[id].OccurrenceEvaluable, $"{id} imported an occurrence set it should not have");
    }

    [Fact]
    public void Occurrence_is_imported_only_where_the_source_and_the_heading_set_match()
    {
        var byId = Authorities().ToDictionary(authority => authority.Id, StringComparer.Ordinal);

        // Five authorities qualify, and all were checked against the source bytes and the count.
        Assert.True(byId["DOC-0001"].OccurrenceEvaluable);
        Assert.True(byId["DOC-0252"].OccurrenceEvaluable);
        Assert.True(byId["DOC-0205"].OccurrenceEvaluable);
        Assert.True(byId["DOC-0256"].OccurrenceEvaluable);
        Assert.True(byId["DOC-0258"].OccurrenceEvaluable);
        Assert.Equal(7, byId["DOC-0001"].Claims.Count);
        Assert.Equal(42, byId["DOC-0252"].Claims.Count);
        Assert.Equal(72, byId["DOC-0205"].Claims.Count);
        Assert.Equal(34, byId["DOC-0256"].Claims.Count);
        Assert.Equal(37, byId["DOC-0258"].Claims.Count);
        // DOC-0256 is bound like DOC-0252: structured atoms of the PDF, no character spans.
        Assert.False(byId["DOC-0256"].CharacterSpanEvaluable);

        // DOC-0001 binds with UTF-16 spans; DOC-0252 binds by alias and selection mode. One root
        // does not mean one coordinate system. DOC-0205 mixes both within itself: 71 of its 72
        // entries carry a promoted UTF-16 span, and its one added entry (the document title) does
        // not - promoted text without an independently re-verified offset.
        Assert.True(byId["DOC-0001"].CharacterSpanEvaluable);
        Assert.False(byId["DOC-0252"].CharacterSpanEvaluable);
        Assert.True(byId["DOC-0205"].CharacterSpanEvaluable);
        // DOC-0258's every heading is a whole paragraph of its regenerated source, so each span is
        // the full alias - measured, not promoted.
        Assert.True(byId["DOC-0258"].CharacterSpanEvaluable);

        // Everything else carries semantic truth and says plainly that it has no bindings.
        foreach (var authority in Authorities().Where(item => item.Id is not ("DOC-0001" or "DOC-0123" or "DOC-0133" or "DOC-0252" or "DOC-0205" or "DOC-0255" or "DOC-0256" or "DOC-0258" or "DOC-0259" or "SRC-029" or "SRC-041" or "SRC-042" or "SRC-044" or "SRC-053" or "SRC-054" or "SRC-055" or "SRC-089" or "SRC-095")))
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
        CanonicalGoldRegistry.RequireCapability("DOC-0264", GoldCapability.SemanticCount);
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0264", GoldCapability.SemanticClaims));
        CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.Occurrence);
        CanonicalGoldRegistry.RequireCapability("DOC-0001", GoldCapability.CharacterSpan);
        // DOC-0205's occurrence set was migrated in - it now claims both which and how many.
        CanonicalGoldRegistry.RequireCapability("DOC-0205", GoldCapability.SemanticClaims);
        CanonicalGoldRegistry.RequireCapability("DOC-0205", GoldCapability.Occurrence);
        CanonicalGoldRegistry.RequireCapability("DOC-0205", GoldCapability.CharacterSpan);

        // DOC-0252 binds by alias, so a character-span score would be invented.
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.CharacterSpan));
        Assert.Throws<InvalidOperationException>(
            () => CanonicalGoldRegistry.RequireCapability("DOC-0264", GoldCapability.Occurrence));
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
    private static readonly string[] OccurrenceCohort = ["DOC-0001", "DOC-0123", "DOC-0133", "DOC-0205", "DOC-0252", "DOC-0255", "DOC-0256", "DOC-0258", "DOC-0259", "SRC-029", "SRC-041", "SRC-042", "SRC-044", "SRC-053", "SRC-054", "SRC-055", "SRC-089", "SRC-095"];

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

    /// <summary>
    /// The request schema as the engine will send it, hashed canonically. Computed here rather than
    /// pinned as a literal so it cannot be copied forward from an older contract.
    /// </summary>
    internal static string SemanticContractSha256() =>
        CanonicalArtifactHash.OfText(JsonSerializer.Serialize(
            CanonicalSemanticContract.Schema(), FreezeArtifact.Json));

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
        // Null, not 1.0. Perfect agreement over nothing is a stronger claim than silence, and it
        // would sit in a table beside a document that really was measured.
        Assert.Null(evaluation.SemanticRole.Accuracy);
        Assert.Equal("NOT_ADJUDICATED", evaluation.SemanticRole.Status);
    }

    private static PdfBoundOccurrence Bound(string alias, int start, int end, string? role) =>
        new([new CanonicalSemanticBoundPart(alias, alias, 0, "text", start, end)], role!, alias, null);

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
            var structured = root.GetProperty("capabilities").TryGetProperty("structuredSourcePartsEvaluable", out var flag)
                && flag.GetBoolean();

            // Which producer "the lane that will run" actually is depends on the authority's own
            // declared profile, not on its media type - a PDF authority may be either.
            var runtime = structured
                ? PdfSourceAdapter
                    .Build(TestRepository.Path(source.GetProperty("sourcePath").GetString()!))
                    .SourceAliasUniverseHash
                : RuntimeSourceUniverse(
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
        // DOC-0252's own reconciliation (review-pack hash 5dd617b2... against the legacy runtime
        // universe cb3c9af6...) belongs to the legacy-occurrence chapter of its history, preserved
        // in that predecessor Gold's own bytes (goldSha256 51e2f708..., unchanged on disk, and in
        // git history) rather than restated here: the authority DOC-0252 now resolves as has moved
        // to the structured profile, and its own active universe is the structured one. What this
        // Gold keeps instead is the fact of that move - one predecessor hash, not a second active
        // universe identity.
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        var provenance = gold.RootElement.GetProperty("provenance").EnumerateArray().ToArray();

        var predecessor = Assert.Single(provenance,
            item => item.GetProperty("role").GetString() == "MIGRATION_PREDECESSOR");
        Assert.Equal("51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65",
            predecessor.GetProperty("sha256").GetString());
        Assert.DoesNotContain(provenance,
            item => item.GetProperty("role").GetString() == "SOURCE_UNIVERSE_RECONCILIATION");

        // The predecessor Gold file itself is untouched: same bytes, same hash, still resolvable
        // directly by path for historical replay - it is simply no longer what "DOC-0252" resolves
        // to by id.
        var predecessorPath = TestRepository.Path(
            "eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json"
                .Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(predecessorPath), "the predecessor Gold must remain on disk as history");
        Assert.Equal("51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65",
            CanonicalArtifactHash.OfTextFile(predecessorPath));

        // DOC-0001 needed no reconciliation and has not migrated: its frozen hash already was, and
        // remains, the runtime one.
        using var docx = CanonicalGoldRegistry.Resolve("DOC-0001");
        Assert.DoesNotContain(docx.RootElement.GetProperty("provenance").EnumerateArray(),
            item => item.GetProperty("role").GetString() is "SOURCE_UNIVERSE_RECONCILIATION" or "MIGRATION_PREDECESSOR");
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

    private static IReadOnlyList<Authority> Authorities() =>
        GoldAuthoredSourceTests.AuthoredFiles().Select(file => Build(file.Path)).ToArray();

    /// <summary>
    /// One authored file in, one consumer view out. Everything a person decided is copied; only what
    /// follows from it - projections, capabilities, the runtime source universe - is derived here.
    /// </summary>
    private static Authority Build(string authoredPath)
    {
        using var authored = JsonDocument.Parse(File.ReadAllText(authoredPath));
        var root = authored.RootElement;
        Assert.Equal(GoldAuthoredSourceTests.AuthoredSchema, root.GetProperty("schemaVersion").GetString());
        var id = root.GetProperty("authorityId").GetString()!;
        var source = root.GetProperty("source");
        var approval = root.GetProperty("approval");

        // Only a current, verified, user-approved Gold becomes active.
        Assert.Equal("VERIFIED", source.GetProperty("sourceLineageStatus").GetString());
        Assert.True(approval.GetProperty("userFinalApproval").GetBoolean());
        Assert.Equal("USER", approval.GetProperty("authority").GetString());

        var total = root.GetProperty("semanticHeadingTotal").GetInt32();
        var sourceSha = source.GetProperty("sourceSha256").GetString()!;
        var sourcePath = source.GetProperty("sourcePath").GetString()!;
        var mediaType = source.GetProperty("mediaType").GetString()!;
        var truthDefinition = approval.GetProperty("truthDefinition").GetString();
        var declared = root.GetProperty("declaredCapabilities");

        var claims = new List<JsonElement>();
        var semanticClaims = new List<object>();
        var occurrenceEvaluable = false;
        var characterSpanEvaluable = false;
        var structuredSourcePartsEvaluable = false;
        string? coordinateSystem = null;
        string? sourceUniverseSha = null;
        var unavailable = Text(root, "occurrenceUnavailableReason");

        var occurrence = root.GetProperty("occurrence");
        if (occurrence.ValueKind == JsonValueKind.Object)
        {
            var bound = occurrence.GetProperty("claims");
            // Fail closed: an itemised Gold that does not add up to its approved total is not Gold.
            Assert.Equal(total, bound.GetArrayLength());
            claims.AddRange(bound.EnumerateArray().Select(item => item.Clone()));
            occurrenceEvaluable = true;
            unavailable = null;

            switch (occurrence.GetProperty("coordinateSystem").GetString())
            {
                case "STRUCTURED_SOURCE_PARTS":
                    semanticClaims.AddRange(bound.EnumerateArray().Select(item => (object)new
                    {
                        sourceParts = item.GetProperty("sourceParts").Clone(),
                        semanticRole = Text(item, "semanticRole"),
                    }));
                    structuredSourcePartsEvaluable = true;
                    // Character spans are evaluable only where every claim records each part's
                    // resolved UTF-16 span; the binder identity alone is not promoted to one.
                    characterSpanEvaluable = bound.GetArrayLength() > 0 && bound.EnumerateArray().All(item =>
                        item.TryGetProperty("boundParts", out var parts)
                        && parts.EnumerateArray().All(part => part.TryGetProperty("utf16Span", out _)));
                    coordinateSystem = "STRUCTURED_SOURCE_PART_TUPLE";
                    sourceUniverseSha = PdfSourceAdapter
                        .Build(TestRepository.Path(sourcePath)).SourceAliasUniverseHash;
                    break;
                case "SOURCE_ALIAS":
                    // Projected, not re-decided: alias, text and role exactly as recorded.
                    semanticClaims.AddRange(bound.EnumerateArray().Select(item => (object)new
                    {
                        sourceAlias = Text(item, "sourceAlias"),
                        verbatimText = Text(item, "verbatimText") ?? Text(item, "exactText"),
                        selectionMode = Text(item, "selectionMode"),
                        semanticRole = Text(item, "semanticRole"),
                    }));
                    characterSpanEvaluable = bound.EnumerateArray().Any(item => item.TryGetProperty("utf16Span", out _));
                    coordinateSystem = characterSpanEvaluable
                        ? "SOURCE_ALIAS_PLUS_UTF16_SPAN"
                        : "SOURCE_ALIAS_PLUS_SELECTION_MODE";
                    sourceUniverseSha = RuntimeSourceUniverse(TestRepository.Path(sourcePath), mediaType, sourceSha);
                    break;
                default:
                    throw new InvalidOperationException($"{id}: unknown coordinate system");
            }
        }

        var provenance = root.GetProperty("provenance").EnumerateArray()
            .Select(item => new ProvenanceItem(
                item.GetProperty("path").GetString()!,
                item.GetProperty("sha256").GetString()!,
                item.GetProperty("role").GetString()!))
            .ToList();

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
                fileName = source.GetProperty("fileName").GetString(),
                mediaType,
                sourcePath,
                sourceSha256 = sourceSha,
                sourceLineageStatus = source.GetProperty("sourceLineageStatus").GetString(),
            },
            approval = new
            {
                authority = "USER",
                userFinalApproval = true,
                approvalBasis = Text(approval, "approvalBasis"),
                approvedAt = Text(approval, "approvedAt"),
                truthDefinition,
                semanticTruthReusedFrom = approval.GetProperty("semanticTruthReusedFrom").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray(),
                reReviewedDuringConsolidation = false,
            },
            semantic = new
            {
                headingSetExhaustive = string.Equals(truthDefinition, "ALL_TRUE_HEADING_OCCURRENCES", StringComparison.Ordinal),
                approvedSemanticTotal = total,
                semanticHeadingTotal = total,
                // Row-level identities exist only where a reviewer materialised them. Where the
                // approved authority is a count, this stays empty: a total says how many headings
                // there are, never which ones.
                materializedSemanticClaims = semanticClaims.Count,
                claims = semanticClaims.ToArray(),
            },
            occurrence = new
            {
                occurrenceEvaluable,
                characterSpanEvaluable,
                bindingCoordinateSystem = coordinateSystem,
                sourceUniverseSha256 = sourceUniverseSha,
                unavailableReason = unavailable,
                claims = claims.ToArray(),
            },
            capabilities = CapabilitiesOf(
                occurrenceEvaluable, characterSpanEvaluable, semanticClaims.Count == total && total > 0,
                declared.GetProperty("visualBindingEvaluable").GetBoolean(),
                declared.GetProperty("hierarchyEvaluable").GetBoolean(),
                structuredSourcePartsEvaluable),
            provenance = provenance
                .Select(item => new { path = item.Path, sha256 = item.Sha256, role = item.Role })
                .ToArray(),
            providerCalls = 0,
            modelCalls = 0,
        };

        return new Authority(
            id, total, semanticClaims.Count, sourceSha, occurrenceEvaluable, characterSpanEvaluable,
            declared.GetProperty("visualBindingEvaluable").GetBoolean(),
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
            return PdfSourceAdapter.Build(path).SourceAliasUniverseHash;

        var source = new OpenXmlDocumentSource().Read(path);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        return DocxSourceUniverseHash.Compute(sourceSha, SemanticSourceAliasCatalog.FromCatalog(catalog));
    }

    /// <summary>
    /// The two structured-profile fields are added only where they are true. Every authority that
    /// has not migrated keeps the exact capabilities object it has always had, byte for byte - the
    /// "absent, not false" rule this repository already applies to every other optional fact,
    /// applied here so declaring one authority's new capability cannot move twenty others' hashes.
    /// </summary>
    private static object CapabilitiesOf(
        bool occurrenceEvaluable, bool characterSpanEvaluable, bool semanticClaimsEvaluable,
        bool visualBindingEvaluable, bool hierarchyEvaluable, bool structuredSourcePartsEvaluable)
    {
        var ordered = new Dictionary<string, object>
        {
            ["semanticCountAuthoritative"] = true,
            ["semanticClaimsEvaluable"] = semanticClaimsEvaluable,
            ["occurrenceEvaluable"] = occurrenceEvaluable,
            ["characterSpanEvaluable"] = characterSpanEvaluable,
            ["visualBindingEvaluable"] = visualBindingEvaluable,
            ["hierarchyEvaluable"] = hierarchyEvaluable,
        };
        if (structuredSourcePartsEvaluable)
        {
            ordered["structuredSourcePartsEvaluable"] = true;
            ordered["coordinateProfile"] = "STRUCTURED_SOURCE_PARTS";
        }
        return ordered;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}
