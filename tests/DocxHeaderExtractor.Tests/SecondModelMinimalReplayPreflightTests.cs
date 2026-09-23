using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline authority for a future second-model replay of the already frozen minimal-context arm.
/// The existing Qwen transport captures are the byte authority for the semantic messages; this
/// test derives only a new model-bound transport identity and nine fresh, temporary reservations.
/// No provider transport or historical artifact mutation is possible here.
/// </summary>
public sealed class SecondModelMinimalReplayPreflightTests
{
    private const string PreflightRoot =
        "eval/a99-closed-loop/second-model-minimal-replay-preflight-v1";
    private const string PreflightFile = "second-model-minimal-replay-preflight.v1.json";
    private const string ContextPreflightRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-preflight-v1";
    private const string ContextPreflightFile = "direct-semantic-context-ablation-preflight.v1.json";
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252";
    private const string ScoreArtifact =
        "eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252/direct-semantic-context-ablation-score.v1.json";
    private const string FutureCaptureRoot =
        "eval/a99-closed-loop/direct-semantic-second-model-minimal-replay-v1/DOC-0252";
    private const string AuthorizedBaseCommit =
        "cba024d8d89456344a7627632706b37c2f160ec0";
    private const string QwenComparatorScoreCommit =
        "cba024d8d89456344a7627632706b37c2f160ec0";
    private const string ExpectedScoreSha256 =
        "af543e432f25a2562ba3472935a02981160578091b8951ec3f192cd00c56fdc3";
    private const string QwenModel = "qwen/qwen3.7-flash";
    private const string SecondModelId = "openai/gpt-4.1";
    private const string PromptSha256 =
        "5e8d393c7e78af28a9695011e63cb4bad00505467532bdcf14582e551aa2a387";
    private const string SchemaSha256 =
        "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9";
    private const string ContextPolicyHash =
        "0e2408334ca81e8db83b2211de0540e2d0d0f27e9cf6e52793c0bb190d814343";
    private const string ResponseFormat = TransportCompatibility.JsonObjectResponseFormat;
    private const int Repeats = 3;
    private const int ProposedCalls = 9;
    private const int MaxOutputTokens = 32768;

    private static readonly string[] Packs = ["PACK_001", "PACK_005", "PACK_006"];

    [Fact]
    public void Freeze_second_model_minimal_replay_preflight_without_provider_calls()
    {
        var head = Git("rev-parse HEAD");
        Assert.True(IsAncestor(AuthorizedBaseCommit, head),
            $"{AuthorizedBaseCommit} is not an ancestor of {head}");

        var scorePath = TestRepository.Path(ScoreArtifact);
        Assert.Equal(ExpectedScoreSha256, Sha256File(scorePath));
        using var score = JsonDocument.Parse(File.ReadAllText(scorePath));
        var scoreRoot = score.RootElement;
        Assert.Equal("9cae02c", scoreRoot.GetProperty("captureCommit").GetString());
        Assert.Equal("CONTEXT_ABLATION_VALID", scoreRoot.GetProperty("validity").GetString());
        Assert.Equal("CONTEXT_TRADEOFF_OBSERVED",
            scoreRoot.GetProperty("primaryClassification").GetString());
        Assert.True(scoreRoot.GetProperty("onlyContextChanged").GetBoolean());
        Assert.Equal(0, scoreRoot.GetProperty("scoringModelCalls").GetInt32());
        Assert.Equal(0, scoreRoot.GetProperty("scoringProviderCalls").GetInt32());

        using var contextPreflight = JsonDocument.Parse(File.ReadAllText(
            TestRepository.Path(Path.Combine(ContextPreflightRoot, ContextPreflightFile))));
        var contextRoot = contextPreflight.RootElement;
        Assert.False(contextRoot.GetProperty("providerAuthorized").GetBoolean());
        Assert.Equal(0, contextRoot.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, contextRoot.GetProperty("providerCalls").GetInt32());
        var minimalArm = contextRoot.GetProperty("arms").EnumerateArray()
            .Single(arm => arm.GetProperty("id").GetString() == "MINIMAL_STRUCTURAL_CONTEXT_V1");
        Assert.Equal(ContextPolicyHash, minimalArm.GetProperty("policyHash").GetString());
        Assert.Equal(PromptSha256, contextRoot.GetProperty("authority")
            .GetProperty("promptSha256").GetString());
        Assert.Equal(SchemaSha256, contextRoot.GetProperty("authority")
            .GetProperty("schemaSha256").GetString());
        Assert.Equal("297387401a96bbc8f5f800456b6e4d9f579a1b04cfef92ffca574deff885cf68",
            minimalArm.GetProperty("providerInputPlanHash").GetString());

        var cells = new List<CellAuthority>();
        var qwenProviderInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var secondProviderInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var contextHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var maxTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var expectedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var systemPromptByPack = new Dictionary<string, string>(StringComparer.Ordinal);
        var userMessageByPack = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var repeat in Enumerable.Range(1, Repeats))
        foreach (var pack in Packs)
        {
            var capturePath = TestRepository.Path(Path.Combine(
                CaptureRoot, "minimal-structural-context-v1", $"r{repeat}",
                $"{pack}.transport-capture.v1.json"));
            using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
            var root = capture.RootElement;
            Assert.Equal("MINIMAL_STRUCTURAL_CONTEXT_V1", root.GetProperty("arm").GetString());
            Assert.Equal(repeat, root.GetProperty("repeat").GetInt32());
            Assert.Equal(pack, root.GetProperty("pack").GetString());
            Assert.Equal(QwenModel, root.GetProperty("model").GetString());
            Assert.Equal("OpenRouter", root.GetProperty("providerRoute").GetString());
            Assert.Equal(ResponseFormat, root.GetProperty("responseFormat").GetString());
            Assert.Equal(ContextPolicyHash, root.GetProperty("policyHash").GetString());
            Assert.Equal(minimalArm.GetProperty("packContextHashes").GetProperty(pack).GetString(),
                root.GetProperty("contextHash").GetString());
            Assert.Equal(minimalArm.GetProperty("providerInputHashes").GetProperty(pack).GetString(),
                root.GetProperty("providerInputHash").GetString());
            Assert.Equal(minimalArm.GetProperty("providerInputPlanHash").GetString(),
                root.GetProperty("planHash").GetString());
            Assert.Equal("TRANSPORT_CAPTURE_COMPLETE",
                root.GetProperty("transportCaptureStatus").GetString());

            var systemPrompt = Decode(root, "systemPromptUtf8Base64");
            var userMessage = Decode(root, "userMessageUtf8Base64");
            var qwenInputHash = SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt, userMessage }));
            Assert.Equal(root.GetProperty("providerInputHash").GetString(), qwenInputHash);
            Assert.Equal(PromptSha256, CanonicalArtifactHash.OfText(systemPrompt));
            Assert.True(TransportCompatibility.Validate(systemPrompt, userMessage, ResponseFormat)
                .IsCompatible);

            if (!systemPromptByPack.TryAdd(pack, systemPrompt))
                Assert.Equal(systemPromptByPack[pack], systemPrompt);
            if (!userMessageByPack.TryAdd(pack, userMessage))
                Assert.Equal(userMessageByPack[pack], userMessage);

            var contextHash = root.GetProperty("contextHash").GetString()!;
            var expectedCount = root.GetProperty("expectedItemCount").GetInt32();
            var budget = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                userMessage, expectedCount, MaxOutputTokens);
            Assert.Equal(root.GetProperty("maxTokens").GetInt32(), budget);
            Assert.True(budget > 256);

            contextHashes[pack] = contextHash;
            messageHashes[pack] = SemanticAuthorityTransportCall.Sha256Utf8(userMessage);
            expectedCounts[pack] = expectedCount;
            maxTokens[pack] = budget;
            qwenProviderInputs[pack] = qwenInputHash;

            // The semantic messages are identical; the new authority deliberately binds the
            // model identity into its provider-input identity so the future runner cannot collide
            // with Qwen reservations while still comparing the same prompt/context bytes.
            secondProviderInputs[pack] = SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { model = SecondModelId, systemPrompt, userMessage }));
            cells.Add(new CellAuthority(
                $"r{repeat}/{pack}", repeat, pack, contextHash, qwenInputHash,
                secondProviderInputs[pack], userMessage, systemPrompt));
        }

        Assert.Equal(ProposedCalls, cells.Count);
        Assert.Equal(ProposedCalls, cells.Select(cell => cell.Identity).Distinct().Count());
        Assert.Equal(Packs.Length, qwenProviderInputs.Count);
        Assert.Equal(Packs.Length, secondProviderInputs.Count);
        Assert.All(Packs, pack => Assert.Equal(
            minimalArm.GetProperty("providerInputHashes").GetProperty(pack).GetString(),
            qwenProviderInputs[pack]));

        var secondPlanHash = CanonicalSemanticRequestComposer.Hash(
            string.Join("\0", Packs.Select(pack => secondProviderInputs[pack])));
        var futureSlots = cells.Select(cell => new
        {
            identity = cell.Identity,
            repeat = cell.Repeat,
            pack = cell.Pack,
            path = $"{FutureCaptureRoot}/r{cell.Repeat}/{cell.Pack}.capture-slot.v1.json",
        }).ToArray();
        Assert.All(futureSlots, slot => Assert.False(File.Exists(TestRepository.Path(slot.path))));
        Assert.True(ProbeAtomicReservation(ProposedCalls));

        FreezeArtifact.AssertJson(PreflightRoot, PreflightFile, new
        {
            artifactKind = "a99_second_model_minimal_replay_preflight",
            schemaVersion = "a99-second-model-minimal-replay-preflight-v1",
            documentId = "DOC-0252",
            currentBaseCommit = AuthorizedBaseCommit,
            qwenComparatorScoreCommit = QwenComparatorScoreCommit,
            sourceSha256 = scoreRoot.GetProperty("sourceSha256").GetString(),
            sourceUniverseSha256 = scoreRoot.GetProperty("sourceUniverseSha256").GetString(),
            goldSha256 = scoreRoot.GetProperty("goldSha256").GetString(),
            firstModel = QwenModel,
            secondModelId = SecondModelId,
            arm = "MINIMAL_STRUCTURAL_CONTEXT_V1",
            providerAuthorized = false,
            modelCalls = 0,
            providerCalls = 0,
            promptSha256 = PromptSha256,
            schemaSha256 = SchemaSha256,
            contextPolicyHash = ContextPolicyHash,
            contextHashes,
            qwenProviderInputHashes = qwenProviderInputs,
            secondModelProviderInputHashes = secondProviderInputs,
            secondModelProviderInputPlanHash = secondPlanHash,
            messageHashes,
            messageParity = new
            {
                pack001 = true,
                pack005 = true,
                pack006 = true,
                onlySemanticVariableChanged = "MODEL_ID",
                semanticMessagesByteIdenticalToQwen = true,
            },
            transport = new
            {
                providerRoute = "OpenRouter",
                responseFormat = ResponseFormat,
                temperature = 0,
                reasoning = "none",
                modelFallbacks = "future runner must pin false",
                compatibilityValidatedOffline = true,
                transportDeltas = new[]
                {
                    "model identity changes",
                    "provider-input identity includes second model id",
                    "future runner must disable silent provider fallback",
                },
            },
            expectedItemCounts = expectedCounts,
            maxTokens,
            capture = new
            {
                identitiesRequired = ProposedCalls,
                identitiesFresh = true,
                identitiesDistinct = true,
                identitiesAtomicallyReservable = true,
                permanentReservationsCreated = false,
                slots = futureSlots,
            },
            comparator = new
            {
                f1 = "0/3 DOCUMENT_LABEL; expected NON_STRUCTURAL",
                agenda = "3/3 STRUCTURAL_UNIT",
                f2 = "3/3 NON_STRUCTURAL",
                f3 = "3/3 NON_STRUCTURAL",
                structuralControls = "42/42",
                documentLabelControl = "3/3",
            },
            primaryTarget = "F1_DOCUMENT_LABEL_ATTRACTOR",
            lifecycle = new
            {
                noReserveAllUpfront = true,
                order = "authority -> messages -> model transport -> compatibility -> budget -> identity -> atomic reservation -> provider -> raw capture -> parse",
                noReplacementCalls = true,
                noSafetyMarginCalls = true,
                noAutomaticContinuation = true,
            },
            crossGenreGeneralizationEstablished = false,
            status = "SECOND_MODEL_MINIMAL_REPLAY_AUTHORIZATION_READY",
        });
    }

    private static string Decode(JsonElement root, string property) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty(property).GetString()!));

    private static bool ProbeAtomicReservation(int count)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "a99-second-model-minimal-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < count; index++)
            {
                using var stream = new FileStream(
                    Path.Combine(root, $"slot-{index:000}.reserve"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Flush(true);
            }

            return Directory.GetFiles(root).Length == count;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Sha256File(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool IsAncestor(string candidate, string descendant) =>
        Git($"merge-base --is-ancestor {candidate} {descendant}", allowFailure: true).Length == 0;

    private static string Git(string arguments, bool allowFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = TestRepository.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        if (allowFailure && process.ExitCode != 0) return "not-ancestor";
        return output;
    }

    private sealed record CellAuthority(
        string Identity,
        int Repeat,
        string Pack,
        string ContextHash,
        string QwenProviderInputHash,
        string SecondProviderInputHash,
        string UserMessage,
        string SystemPrompt);
}
