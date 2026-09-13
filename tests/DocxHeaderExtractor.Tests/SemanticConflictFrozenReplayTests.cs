using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline regression for the frozen W2/W3 S0239 conflict. This test reads only frozen
/// predictions and the faithful source; it never opens Gold and never calls a provider.
/// </summary>
public sealed class SemanticConflictFrozenReplayTests
{
    private const string PredictionRoot = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string OutputPath = "eval/a99-closed-loop/semantic-conflict-frozen-replay/DOC-0205/S0239/replay.v1.json";
    private const string SourceSha256 = "a4e028bc53a9753c380a7ae05a87089e855ca7bb520a50ae4aaf9d535b4b1ef9";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void Frozen_W2_W3_role_conflict_is_bindable_without_gold_or_provider()
    {
        var root = RepoRoot();
        var sourceFile = Path.Combine(root, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(sourceFile), $"Missing faithful source: {sourceFile}");
        Assert.Equal(SourceSha256, Sha256(sourceFile));

        var source = new OpenXmlDocumentSource().Read(sourceFile) with { DocumentId = "DOC-0205" };
        var aliases = source.Paragraphs
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => new SemanticSourceAlias(
                $"S{index + 1:0000}", paragraph.SourceId, paragraph.SourceOrdinal, paragraph.Text,
                new StructuralSpan(0, paragraph.Text.Length),
                new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = paragraph.SourceId, ParagraphIndex = paragraph.SourceOrdinal }))
            .ToArray();
        Assert.Equal("S0239", aliases.Single(alias => alias.Alias == "S0239").Alias);

        var repeats = new List<object>();
        foreach (var repeat in new[] { 2, 3 })
        {
            var predictionPath = Path.Combine(root, PredictionRoot.Replace('/', Path.DirectorySeparatorChar),
                $"r{repeat}", "whole-alias", "prediction.v1.json");
            var freezePath = Path.Combine(root, PredictionRoot.Replace('/', Path.DirectorySeparatorChar),
                $"r{repeat}", "whole-alias", "freeze.v1.json");
            var proposals = LoadFrozenProposals(predictionPath, freezePath);
            var conflictInput = proposals.Where(item => item.SourceAlias == "S0239").ToArray();
            Assert.Equal(2, conflictInput.Length);
            Assert.All(conflictInput, item => Assert.True(item.IsHeading));
            Assert.Equal(new[] { "ARTICLE", "CHAPTER" }, conflictInput.Select(item => item.SemanticRole).OrderBy(item => item, StringComparer.Ordinal));

            // Diagnostic representation of the pre-41f31d9 direct-binder behavior: passing both
            // role alternatives to the binder produces one physical binding and one duplicate.
            var oldDirectBound = CanonicalSemanticExactBinder.Bind(conflictInput, aliases, out var oldDirectObservations);
            var oldDirectFailure = oldDirectObservations.Count(item => item.Status != CanonicalSemanticBindingStatus.Bound);

            var normalized = SemanticConflictNormalizer.Normalize(conflictInput, aliases);
            Assert.Empty(normalized.Conflicts);
            var attributeConflict = Assert.Single(normalized.AttributeConflicts);
            var bindingReady = Assert.Single(normalized.BindingReadyProposals);
            Assert.Equal("semanticRole", Assert.Single(attributeConflict.ContestedFields.Keys));
            Assert.Null(bindingReady.SemanticRole);

            var newBound = CanonicalSemanticExactBinder.Bind(normalized.BindingReadyProposals, aliases, out var newObservations);
            var newBindFailure = newObservations.Count(item => item.Status != CanonicalSemanticBindingStatus.Bound);
            var hardValidation = CanonicalSemanticHardBindingValidator.Validate(
                newBound, aliases, SourceSha256, SourceSha256);

            Assert.Single(oldDirectBound);
            Assert.Equal(1, oldDirectFailure);
            Assert.Single(newBound);
            Assert.Equal(1, newObservations.Count(item => item.Status == CanonicalSemanticBindingStatus.Bound));
            Assert.Equal(0, newBindFailure);
            Assert.True(hardValidation.IsValid);

            repeats.Add(new
            {
                repeat = $"R{repeat}",
                frozenPredictionPath = Relative(root, predictionPath),
                rawProposalCount = proposals.Count,
                sourceAlias = "S0239",
                s0239InputProposals = conflictInput.Length,
                oldPath = new
                {
                    normalizerConflictWithheld = true,
                    binderInput = 0,
                    binderOutput = 0,
                    bindFailure = 0,
                    note = "pre-41f31d9 normalizer withheld the conflict before binding"
                },
                oldDirectDuplicateBinder = new
                {
                    binderInput = conflictInput.Length,
                    binderOutput = oldDirectBound.Count,
                    bindFailure = oldDirectFailure
                },
                newPath = new
                {
                    bindingReadyOccurrence = normalized.BindingReadyProposals.Count,
                    attributeConflict = normalized.AttributeConflicts.Count,
                    contestedFields = attributeConflict.ContestedFields.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                    consensusSemanticRole = bindingReady.SemanticRole,
                    binderInput = normalized.BindingReadyProposals.Count,
                    binderOutput = newBound.Count,
                    bindFailure = newBindFailure,
                    hardBindingValid = hardValidation.IsValid,
                    systemLoss = Math.Max(0, normalized.BindingReadyProposals.Count - newBound.Count)
                }
            });
        }

        var all = repeats.Cast<dynamic>().ToArray();
        Assert.All(all, repeat =>
        {
            Assert.Equal(2, (int)repeat.s0239InputProposals);
            Assert.Equal(1, (int)repeat.newPath.bindingReadyOccurrence);
            Assert.Equal(1, (int)repeat.newPath.attributeConflict);
            Assert.Equal(0, (int)repeat.newPath.bindFailure);
            Assert.Equal(0, (int)repeat.newPath.systemLoss);
        });

        var artifactPath = Path.Combine(root, OutputPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-semantic-conflict-frozen-replay-v1",
            status = "COMPLETE",
            documentId = "DOC-0205",
            physicalSourceIdentity = "S0239",
            sourceSha256 = SourceSha256,
            sourcePath = SourcePath,
            frozenInputRoot = PredictionRoot,
            repeats = new[] { "R2", "R3" },
            modelCalls = 0,
            providerCalls = 0,
            goldRead = false,
            goldReadBeforeFreeze = false,
            acceptance = new
            {
                s0239InputProposals = 2,
                bindingReadyOccurrence = 1,
                attributeConflict = 1,
                contestedField = "semanticRole",
                consensusSemanticRole = (string?)null,
                binderInput = 1,
                binderOutput = 1,
                bindFailure = 0,
                systemLoss = 0
            },
            perRepeat = repeats
        }, JsonOptions));
    }

    private static IReadOnlyList<CanonicalSemanticProposal> LoadFrozenProposals(string predictionPath, string freezePath)
    {
        Assert.True(File.Exists(predictionPath), $"Missing frozen prediction: {predictionPath}");
        Assert.True(File.Exists(freezePath), $"Missing frozen freeze: {freezePath}");
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        var freezeRoot = freeze.RootElement;
        Assert.False(freezeRoot.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(Sha256(predictionPath), freezeRoot.GetProperty("predictionSha256").GetString());
        using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
        return JsonSerializer.Deserialize<IReadOnlyList<CanonicalSemanticProposal>>(
            prediction.RootElement.GetProperty("proposals").GetRawText(), JsonOptions) ?? [];
    }

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
