using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free taxonomy of the four true-anchor errors in the frozen H2-C evidence pair.</summary>
public sealed class V5P6TH2CEvidenceErrorTaxonomyTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string ScorePath = Root + "/p6th2c-evidence-paired-gold-score/h2c-evidence-paired-gold-score.v1.json";
    private const string PreflightPath = Root + "/p6th2c-evidence-complete-preflight/h2c-evidence-complete-preflight.v1.json";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string OutputRoot = Root + "/p6th2c-evidence-error-taxonomy";
    private const string BaselineCommit = "dfe96aae9c2a73102dd942b9a5c21df5c20641bd";

    private sealed record Target(string DocumentId, string Anchor, string Taxonomy, string AOutcome, string BOutcome,
        string PairedTransition, string Finding);

    private static readonly Target[] Targets =
    [
        new("SRC-089", "O17", "UNDEREXTENT_CROSS_BLOCK_TITLE_COMPLEMENT_MISREAD_AS_NEW_HEADING", "UNDEREXTENT", "UNDEREXTENT", "BOTH_WRONG",
            "The chapter label and centered title line are one Gold heading. The model stops after the label and explicitly calls the complementary title line a new heading in both arms."),
        new("SRC-089", "O19", "OVEREXTENT_HEADING_INTO_NUMBERED_PROSE", "OVEREXTENT", "OVEREXTENT", "BOTH_WRONG",
            "The Article 1 heading is followed by numbered legal prose. Both arms absorb the first provision; A continues through the second provision, while B stops only before it."),
        new("SRC-041", "O4", "ARM_A_OVEREXTENT_B_EXACT_AT_TITLE_TO_DATE_TRANSITION", "OVEREXTENT", "EXACT", "A_WRONG_TO_B_RIGHT",
            "A absorbs the date and unit note after a two-line table title. B ends at the Gold boundary immediately before a visibly smaller italic date line; the treatment changed multiple evidence groups, so typography is a lead hypothesis, not an isolated cause."),
        new("DOC-0256", "O1", "OVEREXTENT_TITLE_INTO_DATE_AND_MEETING_MODE", "OVEREXTENT", "OVEREXTENT", "BOTH_WRONG",
            "Both arms extend the two-line meeting title through its date and meeting-mode lines, then stop before the first agenda heading. Those four front-matter lines share font family, size, weight, centering, and regular vertical rhythm."),
    ];

    [Fact]
    public void Four_true_anchor_errors_are_frozen_with_source_fact_taxonomy()
    {
        var repo = TestRepository.Root();
        var scoreBytes = File.ReadAllBytes(TestRepository.Path(ScorePath));
        var preflightBytes = File.ReadAllBytes(TestRepository.Path(PreflightPath));
        using var score = JsonDocument.Parse(scoreBytes);
        using var preflight = JsonDocument.Parse(preflightBytes);
        var scoreRows = score.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var sourceAuthorities = preflight.RootElement.GetProperty("authorities").GetProperty("sourceAuthorities")
            .EnumerateArray().ToDictionary(value => value.GetProperty("DocumentId").GetString()!, StringComparer.Ordinal);
        var goldAuthorities = score.RootElement.GetProperty("authority").GetProperty("goldAuthorities")
            .EnumerateArray().ToDictionary(value => value.GetProperty("documentId").GetString()!, StringComparer.Ordinal);

        var cases = Targets.Select(target => BuildCase(repo, target, scoreRows, sourceAuthorities, goldAuthorities)).ToArray();

        Assert.Equal(4, cases.Length);
        Assert.Equal(3, Targets.Count(value => value.PairedTransition == "BOTH_WRONG"));
        Assert.Single(Targets, value => value.PairedTransition == "A_WRONG_TO_B_RIGHT");
        Assert.DoesNotContain(Targets, value => value.PairedTransition == "A_CORRECT_TO_B_WRONG");
        Assert.Equal(1, Targets.Count(value => value.AOutcome == "UNDEREXTENT" || value.BOutcome == "UNDEREXTENT"));
        Assert.Equal(4, Targets.Count(value => value.AOutcome != "EXACT"));
        Assert.Equal(3, Targets.Count(value => value.BOutcome != "EXACT"));

        FreezeArtifact.AssertJson(OutputRoot, "h2c-evidence-error-taxonomy.v1.json", new
        {
            schemaVersion = "v5-p6th2c-evidence-error-taxonomy-v1",
            status = "PROVIDER_FREE_SOURCE_FACT_TAXONOMY_OF_FROZEN_TRUE_ANCHOR_ERRORS",
            authority = new
            {
                baselineCommit = BaselineCommit,
                experiment = "H2C_EVIDENCE_COMPLETE_A_CURRENT_PROJECTION_VS_B_COMPOSITE_RICH_EVIDENCE; NOT_THE_PRIOR_CLEAN_V1_V2_TREATMENT",
                pairedScorePath = ScorePath,
                pairedScoreSha256 = Hash(scoreBytes),
                preflightPath = PreflightPath,
                preflightSha256 = Hash(preflightBytes),
                canonicalGold = goldAuthorities.Values.OrderBy(value => value.GetProperty("documentId").GetString(), StringComparer.Ordinal)
                    .Select(value => new
                    {
                        documentId = value.GetProperty("documentId").GetString(),
                        canonicalGoldPath = value.GetProperty("canonicalGoldPath").GetString(),
                        goldSha256 = value.GetProperty("goldSha256").GetString(),
                        sourceSha256 = value.GetProperty("sourceSha256").GetString(),
                    }),
                gold = "READ_ONLY_VIA_FROZEN_PAIRED_SCORE; NOT_MODIFIED",
                rawResponses = "NOT_COPIED_OR_CHANGED; THIS_SANITIZED_ARTIFACT_OMITS_SOURCE_TEXT_AND_RAW_SSE",
            },
            safety = new { providerCalls = 0, retry = 0, repair = false, fallback = false, goldMutation = "NONE", runtimeChanged = false },
            summary = new
            {
                trueAnchorErrorCases = 4,
                wrongInBothArms = 3,
                armAWrongArmBRight = 1,
                armARightArmBWrong = 0,
                sharedUnderextent = 1,
                sharedOverextent = 2,
                armAOnlyOverextent = 1,
                falseG2AAnchorsExcluded = true,
                selectionBasis = "POST_HOC_TRUE_ANCHOR_ERRORS_FROM_FROZEN_A_OF_THIS_EVIDENCE_COMPLETE_A_B_EXPERIMENT",
                taxonomyIsDiagnostic = true,
                causalAttribution = "NOT_ESTABLISHED; ARM B CHANGED THE FULL EVIDENCE BUNDLE",
            },
            evidenceInterpretation = new
            {
                likelyBSignal = "The only corrected case has a strong typography transition at the Gold exit (16 pt regular title to 10 pt italic date); this is correlation within a composite treatment, not component attribution.",
                typographyCounterexamples = "The shared SRC-089 underextent has same-size/same-family bold lines; DOC-0256 overextent has same-size/same-family/same-weight centered title, date, and meeting-mode lines.",
                geometryAndSpacing = "No common geometric separator is visible: SRC-089 needs a left-aligned label joined to a centered title; DOC-0256 keeps the title and metadata centered; their top-coordinate gaps do not uniquely mark the Gold boundary.",
                sourceBlockTopology = "Parser block IDs are available in B but all relevant lines are separate blocks, including true multipart title lines; block identity cannot be used as a hard boundary rule.",
                structuralScope = "The canonical evidence label is document_body for these local windows and is not discriminative. Do not add semantic scopes such as TITLE, DATE, BODY, or HEADING as supposedly neutral facts.",
                firstOutsideRole = "Descriptive model output at its predicted boundary only; not a Gold role label and not scored for role accuracy.",
            },
            ablationRecommendation = new
            {
                firstDiagnostic = "TYPOGRAPHY_ONLY, compared with the frozen Arm A projection, on the four fixed error anchors; especially test whether it reproduces the SRC-041/O4 correction.",
                laterOneGroupArms = new[] { "GEOMETRY_ONLY", "SPACING_ONLY", "SOURCE_BLOCK_TOPOLOGY_ONLY" },
                structuralScopeArm = "Use source-native block topology only if that is what is meant; the current document_body label has no local variation and semantic role labels remain forbidden.",
                interpretationLimit = "These four cases were selected after observing errors. Any ablation on them is diagnostic only; a claimed score gain requires a preregistered full 31-request cohort or a separately frozen held-out cohort.",
                noProviderAuthorizationImplied = true,
            },
            cases,
        });
    }

    private static object BuildCase(string repo, Target target, IReadOnlyList<JsonElement> scoreRows,
        IReadOnlyDictionary<string, JsonElement> sourceAuthorities, IReadOnlyDictionary<string, JsonElement> goldAuthorities)
    {
        var row = scoreRows.Single(value => value.GetProperty("DocumentId").GetString() == target.DocumentId &&
                                             value.GetProperty("Anchor").GetString() == target.Anchor);
        var source = sourceAuthorities[target.DocumentId];
        var goldAuthority = goldAuthorities[target.DocumentId];
        var sourceHash = source.GetProperty("SourceSha256").GetString()!;
        var snapshotPath = Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json");
        using var snapshot = JsonDocument.Parse(File.ReadAllBytes(snapshotPath));
        Assert.Equal(sourceHash, snapshot.RootElement.GetProperty("SourceSha256").GetString());

        var atoms = snapshot.RootElement.GetProperty("Atoms").EnumerateArray().ToArray();
        var evidence = snapshot.RootElement.GetProperty("Evidence").EnumerateArray()
            .ToDictionary(value => value.GetProperty("SourceAlias").GetString()!, StringComparer.Ordinal);
        var blocks = snapshot.RootElement.GetProperty("LayoutBlockByAtom");
        var anchorAlias = row.GetProperty("AnchorAlias").GetString()!;
        var anchorIndex = Array.FindIndex(atoms, value => value.GetProperty("alias").GetString() == anchorAlias);
        Assert.True(anchorIndex >= 0, $"taxonomy-anchor-not-in-source-snapshot:{target.DocumentId}:{target.Anchor}");
        var anchorOrdinal = atoms[anchorIndex].GetProperty("ordinal").GetInt32();
        var anchorHandle = ParseHandle(target.Anchor);
        var goldAliases = row.GetProperty("GoldExtent").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var aEndAlias = row.GetProperty("A").GetProperty("PredictedEndAlias").GetString();
        var bEndAlias = row.GetProperty("B").GetProperty("PredictedEndAlias").GetString();
        Assert.NotNull(aEndAlias);
        Assert.NotNull(bEndAlias);
        var aEndOrdinal = FindOrdinal(atoms, aEndAlias!);
        var bEndOrdinal = FindOrdinal(atoms, bEndAlias!);
        var goldEndOrdinal = goldAliases.Max(alias => FindOrdinal(atoms, alias));
        var windowEndOrdinal = Math.Min(atoms.Length - 1, Math.Max(goldEndOrdinal, Math.Max(aEndOrdinal, bEndOrdinal)) + 1);
        var windowStartIndex = anchorIndex;
        var windowEndIndex = Array.FindLastIndex(atoms, value => value.GetProperty("ordinal").GetInt32() <= windowEndOrdinal);

        var localSource = Enumerable.Range(windowStartIndex, windowEndIndex - windowStartIndex + 1).Select(index =>
        {
            var atom = atoms[index];
            var ordinal = atom.GetProperty("ordinal").GetInt32();
            var alias = atom.GetProperty("alias").GetString()!;
            var sourceId = atom.GetProperty("sourceId").GetString()!;
            var fact = evidence[alias];
            var style = fact.GetProperty("StyleFacts");
            var sourceCoordinates = ParseSourceCoordinates(sourceId);
            var blockProperty = blocks.TryGetProperty(sourceId, out var blockValue) ? blockValue.GetString() : null;
            return new
            {
                occurrence = $"O{anchorHandle + ordinal - anchorOrdinal}",
                offsetFromAnchor = ordinal - anchorOrdinal,
                alias,
                goldMembership = goldAliases.Contains(alias) ? "GOLD_MEMBER" : "GOLD_OUTSIDE",
                armAMembership = ordinal <= aEndOrdinal ? "SELECTED_MEMBER" : "OUTSIDE",
                armBMembership = ordinal <= bEndOrdinal ? "SELECTED_MEMBER" : "OUTSIDE",
                page = atom.GetProperty("page").GetInt32(),
                sourceCoordinates,
                parserLayoutBlockId = blockProperty,
                structuralScope = fact.GetProperty("StructuralScope").GetString(),
                typography = new
                {
                    fontSize = style.GetProperty("fontSize").GetDouble(),
                    fontSizeToBodyRatio = style.GetProperty("fontSizeToBodyRatio").GetDouble(),
                    boldRatio = style.GetProperty("boldRatio").GetDouble(),
                    italicRatio = style.GetProperty("italicRatio").GetDouble(),
                    lineCount = style.GetProperty("lineCount").GetInt32(),
                    dominantFontName = style.GetProperty("Typography").GetProperty("dominantFontName").GetString(),
                },
                verticalPosition = fact.GetProperty("LocationFacts").GetProperty("verticalPosition").GetDouble(),
                observedSourceEvidence = fact.GetProperty("ObservedEvidence").EnumerateArray().Select(value => value.GetString()).ToArray(),
            };
        }).ToArray();

        var a = row.GetProperty("A");
        var b = row.GetProperty("B");
        Assert.Equal(target.AOutcome, a.GetProperty("Outcome").GetString());
        Assert.Equal(target.BOutcome, b.GetProperty("Outcome").GetString());
        Assert.Equal(target.PairedTransition, row.GetProperty("PairedTransition").GetString());

        return new
        {
            documentId = target.DocumentId,
            anchor = target.Anchor,
            anchorAlias,
            sourceSha256 = sourceHash,
            sourceUniverseSha256 = source.GetProperty("SourceUniverseSha256").GetString(),
            canonicalGoldSha256 = goldAuthority.GetProperty("goldSha256").GetString(),
            taxonomy = target.Taxonomy,
            finding = target.Finding,
            AOutcome = a.GetProperty("Outcome").GetString(),
            BOutcome = b.GetProperty("Outcome").GetString(),
            PairedTransition = row.GetProperty("PairedTransition").GetString(),
            goldExtent = row.GetProperty("GoldExtent").EnumerateArray().Select(value => value.GetString()).ToArray(),
            pair = new
            {
                armA = new
                {
                    outcome = a.GetProperty("Outcome").GetString(),
                    predictedEndOccurrence = a.GetProperty("PredictedEndOccurrence").GetString(),
                    predictedEndAlias = a.GetProperty("PredictedEndAlias").GetString(),
                    firstOutsideOccurrence = a.GetProperty("FirstOutsideOccurrence").GetString(),
                    firstOutsideRole = a.GetProperty("FirstOutsideRole").GetString(),
                },
                armB = new
                {
                    outcome = b.GetProperty("Outcome").GetString(),
                    predictedEndOccurrence = b.GetProperty("PredictedEndOccurrence").GetString(),
                    predictedEndAlias = b.GetProperty("PredictedEndAlias").GetString(),
                    firstOutsideOccurrence = b.GetProperty("FirstOutsideOccurrence").GetString(),
                    firstOutsideRole = b.GetProperty("FirstOutsideRole").GetString(),
                },
                transition = row.GetProperty("PairedTransition").GetString(),
                promptTokens = new { armA = row.GetProperty("PromptTokensA").GetInt32(), armB = row.GetProperty("PromptTokensB").GetInt32() },
                providerBodySha256 = new
                {
                    armA = row.GetProperty("ProviderBodySha256A").GetString(),
                    armB = row.GetProperty("ProviderBodySha256B").GetString(),
                },
            },
            localSource,
        };
    }

    private static int FindOrdinal(IReadOnlyList<JsonElement> atoms, string alias) =>
        atoms.Single(value => value.GetProperty("alias").GetString() == alias).GetProperty("ordinal").GetInt32();

    private static int ParseHandle(string handle) => int.Parse(handle.AsSpan(1), CultureInfo.InvariantCulture);

    private static object ParseSourceCoordinates(string sourceId)
    {
        var parts = sourceId.Split('|', 5);
        Assert.True(parts.Length == 5, $"taxonomy-source-id-coordinate-shape-invalid:{sourceId}");
        return new
        {
            topY = double.Parse(parts[1], CultureInfo.InvariantCulture),
            left = double.Parse(parts[2], CultureInfo.InvariantCulture),
            right = double.Parse(parts[3], CultureInfo.InvariantCulture),
            width = double.Parse(parts[3], CultureInfo.InvariantCulture) - double.Parse(parts[2], CultureInfo.InvariantCulture),
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
