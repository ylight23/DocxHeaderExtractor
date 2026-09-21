using System.Text.Json;

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
                semanticEvaluable = true,
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
        CanonicalGoldRegistry.RequireCapability("DOC-0252", GoldCapability.Semantic);
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

    // ---- derivation ---------------------------------------------------------------------------

    private sealed record Authority(
        string Id,
        int Total,
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
        var occurrenceEvaluable = false;
        var characterSpanEvaluable = false;
        string? unavailable = "no occurrence artifact describes this source under the current authority";
        string? coordinateSystem = null;
        string? sourceUniverseSha = null;

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
                occurrenceEvaluable = true;
                characterSpanEvaluable = occurrenceText.Contains("utf16Span", StringComparison.Ordinal);
                coordinateSystem = characterSpanEvaluable
                    ? "SOURCE_ALIAS_PLUS_UTF16_SPAN"
                    : "SOURCE_ALIAS_PLUS_SELECTION_MODE";
                unavailable = null;
                sourceUniverseSha = occurrenceRoot.TryGetProperty("sourceUniverseSha256", out var universe)
                    ? universe.GetString()
                    : null;
                provenance.Add(new(RelativeTo(occurrencePath),
                    CanonicalArtifactHash.OfText(occurrenceText), "OCCURRENCE_AUTHORITY"));
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
                semanticHeadingTotal = total,
                // Empty where the approved authority is a count. A count is what was approved, and
                // materialising rows from it would be inventing identities nobody reviewed.
                headings = Array.Empty<object>(),
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
                semanticEvaluable = true,
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
            id, total, sourceSha, occurrenceEvaluable, characterSpanEvaluable,
            capabilities.TryGetProperty("visualBindingEvaluable", out var vb) && vb.GetBoolean(),
            unavailable, claims, provenance, gold);
    }

    private static string RelativeTo(string absolute) =>
        Path.GetRelativePath(TestRepository.Root(), absolute).Replace(Path.DirectorySeparatorChar, '/');
}
