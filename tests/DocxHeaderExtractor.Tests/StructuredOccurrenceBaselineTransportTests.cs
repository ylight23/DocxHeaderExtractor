using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99-S2P-STRUCTURED-BASELINE-V1: the successor to <see cref="OccurrenceBaselineTransportTests"/>'s
/// A99-S2P-OCCURRENCE-BASELINE-V1, routed through <see cref="PdfSemanticAuthorityProfile.StructuredSourceParts"/>
/// for DOC-0252 instead of the legacy occurrence authority.
/// <para>
/// Every hash this file gates on was frozen and cross-checked over several prior tasks, culminating
/// in an independent offline replay of the b187cc2 legacy baseline through the unchanged
/// LEGACY_OCCURRENCE path (<see cref="OccurrenceBaselineScoringTests"/>). This file spends the
/// provider calls that baseline's structured successor was authorized for - 21 primary calls
/// (DOC-0001 DOCX x1, DOC-0252 structured PDF x6, three repeats), hard capped at 27. Scoring the
/// resulting bundles against structured Gold's sourceParts claims is intentionally a separate,
/// offline step: this file's only job, like its predecessor's, is to capture what the provider
/// actually returned, under gates that throw before the first request rather than after.
/// </para>
/// </summary>
public sealed class StructuredOccurrenceBaselineTransportTests
{
    private const string RunVariable = "A99_S2P_STRUCTURED_RUN";
    private const int MaxProviderCalls = 27;
    private const int Repetitions = 3;
    private const string Model = "qwen/qwen3.7-flash";
    private const string EvaluatorId = "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role";

    // DOC-0001 is DOCX; the structured/legacy PDF profile split does not touch it. Its prompt is
    // the unmodified core prompt (DocxAliasSpan carries no PromptClause).
    private const string Doc0001ExpectedPrompt = "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";

    // DOC-0252 now runs under the structured coordinate contract, whose prompt is the core prompt
    // plus SemanticCoordinateContract.PdfStructuredSourceParts.PromptClause.
    private const string Doc0252ExpectedPrompt = "2207221eb8782c13296023aefe5b3cfe9a771eba652f029745948e2534fe580e";

    // The meaning contract (what a claim says), shared by every lane and unmoved by this migration.
    private const string ExpectedSemanticContract = "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e";

    // The structured coordinate contract (where a claim points), specific to DOC-0252's lane here.
    private const string ExpectedStructuredCoordinateContract =
        "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";

    private const string ExpectedDoc0252SourceUniverse =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string ExpectedDoc0252Gold =
        "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";

    private static readonly string[] Cohort = ["DOC-0001", "DOC-0252"];

    private const string OutputRoot = "eval/a99-closed-loop/occurrence-baseline-structured-v1";

    [Fact]
    public void All_fail_closed_gates_hold_without_contacting_a_provider()
    {
        var report = VerifyGates();
        Assert.All(report, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_structured_occurrence_baseline()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
        });
        using var budgeted = new BudgetedClassifier(provider, MaxProviderCalls);

        var runs = new List<object>();
        var aborted = (string?)null;

        foreach (var id in Cohort)
        {
            for (var repeat = 1; repeat <= Repetitions && aborted is null; repeat++)
            {
                budgeted.DocumentId = id;
                budgeted.Repeat = repeat;
                budgeted.Stage = "semantic";
                try
                {
                    runs.Add(await RunOnceAsync(id, repeat, budgeted));
                }
                catch (Exception error)
                {
                    aborted = $"{id} r{repeat}: {error.GetType().Name}: {error.Message}";
                }
            }
        }

        Persist(budgeted, runs, gates, aborted);
        Assert.True(budgeted.CallsMade <= MaxProviderCalls);
        Assert.Null(aborted);
    }

    // ---- gates --------------------------------------------------------------------------------

    private static IReadOnlyList<string> VerifyGates()
    {
        var lines = new List<string>();

        foreach (var id in Cohort)
        {
            var entry = CanonicalGoldRegistry.Entry(id);
            using var gold = CanonicalGoldRegistry.Resolve(id); // throws on gold hash mismatch
            var source = gold.RootElement.GetProperty("source");
            var path = TestRepository.Path(source.GetProperty("sourcePath").GetString()!);
            var media = source.GetProperty("mediaType").GetString()!;

            var actualSource = CanonicalArtifactHash.OfBytes(path);
            lines.Add(Check($"{id} goldHash", entry.GoldSha256, entry.GoldSha256));
            lines.Add(Check($"{id} sourceHash", entry.SourceSha256, actualSource));

            var frozenUniverse = gold.RootElement.GetProperty("occurrence")
                .GetProperty("sourceUniverseSha256").GetString()!;
            lines.Add(Check($"{id} sourceUniverse", frozenUniverse, RuntimeUniverse(path, media, actualSource)));

            var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").GetArrayLength();
            var semantic = gold.RootElement.GetProperty("semantic").GetProperty("claims").GetArrayLength();
            lines.Add(Check($"{id} semanticClaims", entry.SemanticHeadingTotal.ToString(), semantic.ToString()));
            lines.Add(Check($"{id} occurrenceClaims", entry.SemanticHeadingTotal.ToString(), claims.ToString()));
            lines.Add(Check($"{id} capabilities",
                "occurrence+claims", entry is { OccurrenceEvaluable: true, SemanticClaimsEvaluable: true }
                    ? "occurrence+claims" : "INSUFFICIENT"));
        }

        // DOC-0252-specific: the two identities that make this run structured rather than legacy.
        lines.Add(Check("doc0252 goldHash", ExpectedDoc0252Gold, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256));
        lines.Add(Check("doc0252 sourceUniverse", ExpectedDoc0252SourceUniverse,
            PdfStructuredSourceAuthorityBuilder.Build(
                TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"
                    .Replace('/', Path.DirectorySeparatorChar))).SourceUniverseSha256));
        lines.Add(Check("doc0252 promptHash", Doc0252ExpectedPrompt,
            CanonicalArtifactHash.OfText(CanonicalSemanticEngine.SystemPromptFor(
                SemanticCoordinateContract.PdfStructuredSourceParts, CanonicalSemanticExperiment.Baseline))));
        lines.Add(Check("doc0252 coordinateContractSha256", ExpectedStructuredCoordinateContract,
            SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash()));

        lines.Add(Check("doc0001 promptHash", Doc0001ExpectedPrompt,
            CanonicalArtifactHash.OfText(CanonicalSemanticEngine.SystemPrompt)));
        lines.Add(Check("semanticContractSha256", ExpectedSemanticContract,
            CanonicalArtifactHash.OfText(JsonSerializer.Serialize(
                CanonicalSemanticContract.Schema(), FreezeArtifact.Json))));
        lines.Add(Check("model", Model, Model));
        lines.Add(Check("evaluator", EvaluatorId, PdfSemanticAuthorityProfile.StructuredSourceParts.EvaluatorId));
        lines.Add(Check("profile", "STRUCTURED_SOURCE_PARTS", PdfSemanticAuthorityProfile.StructuredSourceParts.ProfileId));
        lines.Add(Check("budget", "27", MaxProviderCalls.ToString()));
        return lines;
    }

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    private static string RuntimeUniverse(string path, string mediaType, string sourceSha)
    {
        if (string.Equals(mediaType, "PDF", StringComparison.OrdinalIgnoreCase))
            return PdfStructuredSourceAuthorityBuilder.Build(path).SourceUniverseSha256;

        var source = new OpenXmlDocumentSource().Read(path);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        return DocxSourceUniverseHash.Compute(sourceSha, SemanticSourceAliasCatalog.FromCatalog(catalog));
    }

    // ---- one run ------------------------------------------------------------------------------

    private static async Task<object> RunOnceAsync(string id, int repeat, BudgetedClassifier classifier)
    {
        var entry = CanonicalGoldRegistry.Entry(id);
        using var gold = CanonicalGoldRegistry.Resolve(id);
        var source = gold.RootElement.GetProperty("source");
        var path = TestRepository.Path(source.GetProperty("sourcePath").GetString()!);
        var media = source.GetProperty("mediaType").GetString()!;
        var universeHash = gold.RootElement.GetProperty("occurrence")
            .GetProperty("sourceUniverseSha256").GetString()!;
        var isPdf = string.Equals(media, "PDF", StringComparison.OrdinalIgnoreCase);
        var promptHash = isPdf ? Doc0252ExpectedPrompt : Doc0001ExpectedPrompt;
        var before = classifier.CallsMade;

        var capture = new SemanticAuthorityReplayCaptureRequest(
            new SemanticAuthorityCaptureMetadata(
                media, universeHash, Model, "openrouter", promptHash,
                GoldId: id, GoldHash: entry.GoldSha256, EvaluatorIdentity: EvaluatorId,
                ManifestHash: "A99-S2P-STRUCTURED-BASELINE-V1", RunId: $"{id}-r{repeat}",
                CreatedAt: DateTimeOffset.UtcNow),
            Path.Combine(TestRepository.Path(OutputRoot), id, $"r{repeat}"));

        StructuralAuthorityResult authority;
        if (isPdf)
        {
            authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                path, classifier, CancellationToken.None, replayCapture: capture,
                profile: PdfSemanticAuthorityProfile.StructuredSourceParts);
        }
        else
        {
            var document = new OpenXmlDocumentSource().Read(path);
            var features = NumberingStyleFeatures.FromSourceDocument(document);
            var state = DocxPolicyStateBuilder.Build(
                document, features, new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
            var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            authority = await CanonicalSemanticDocxAuthorityAdapter.RunAsync(
                state, mode, classifier, CancellationToken.None, replayCapture: capture);
        }

        var bundle = authority.ReplayBundle;
        return new
        {
            documentId = id,
            repeat,
            callsSpent = classifier.CallsMade - before,
            headings = authority.Structure.Elements.Count,
            bundleHash = bundle?.BundleHash,
            proposalHash = bundle?.ProposalHash,
            aliasCatalogHash = bundle?.AliasCatalogHash,
            proposals = bundle?.Proposals.Count ?? 0,
            sourceUniverseHash = bundle?.SourceUniverseHash,
            goldHash = bundle?.GoldHash,
        };
    }

    private static void Persist(
        BudgetedClassifier classifier, IReadOnlyList<object> runs,
        IReadOnlyList<string> gates, string? aborted)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            artifactKind = "a99_occurrence_baseline_run",
            schemaVersion = "a99-occurrence-baseline-run-v1",
            experimentId = "A99-S2P-STRUCTURED-BASELINE-V1",
            authorityProfile = "STRUCTURED_SOURCE_PARTS",
            model = Model,
            evaluatorId = EvaluatorId,
            semanticContractSha256 = ExpectedSemanticContract,
            structuredCoordinateContractSha256 = ExpectedStructuredCoordinateContract,
            maxProviderCalls = MaxProviderCalls,
            providerCalls = classifier.CallsMade,
            unusedAllowance = MaxProviderCalls - classifier.CallsMade,
            aborted,
            gates,
            runs,
            callLedger = classifier.Ledger.Select(call => new
            {
                call.Ordinal, call.DocumentId, call.Repeat, call.Stage,
                call.RequestSha256, call.ResponseSha256,
                call.RequestChars, call.ResponseChars, call.ElapsedMs,
            }).ToArray(),
        }, FreezeArtifact.Json);
        File.WriteAllText(Path.Combine(directory, "run.v1.json"), payload.ReplaceLineEndings("\n"));

        var raw = JsonSerializer.Serialize(
            classifier.Ledger.Select(call => new { call.Ordinal, call.DocumentId, call.Repeat, call.RawResponse }),
            FreezeArtifact.Json);
        File.WriteAllText(Path.Combine(directory, "raw-responses.v1.json"), raw.ReplaceLineEndings("\n"));
    }
}
