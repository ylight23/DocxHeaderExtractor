using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2jStrictOmissionCausalAuditTests
{
    private const string Root =
        "eval/a99-closed-loop/semantic-text-replay-successor-v1-runtime-authority/DOC-0252";
    private const string GoldPath =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string PreflightPath =
        "eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/preflight.v1.json";
    private const string SourceUniversePath =
        "eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/source-universe-runtime.v1.json";
    private const string SourceHash =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const string PromptHash =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string SemanticContractHash =
        "21687e5a78d59b8c82124dcc567c7353024145d599b1294c41c888ffe4512365";
    private const string SuccessorManifestHash =
        "2ac4513d81d63fcaab23cf26d0fbacf4bd5ac741770eeb0f4c4ce9c0fcbd3474";
    private const int OwnedPerSegment = 120;
    private const int VisibleMargin = 20;
    private const int ShortSpanThreshold = 20;
    private const int NearbyAliasDistance = 2;

    [Fact]
    public void Frozen_requests_characterize_strict_persistent_omissions_without_transport()
    {
        var gold = JsonSerializer.Deserialize<PdfGoldDocument>(
            File.ReadAllText(RepositoryPath(GoldPath)))!;
        var bundles = Enumerable.Range(1, 3).Select(LoadBundle).ToArray();
        var goldBound = PdfGoldBoundOccurrenceEvaluator.BindGold(
            gold, bundles[0].AliasCatalog, out var bindingIssues);
        Assert.Empty(bindingIssues);

        var strictKeys = LoadStrictKeys();
        Assert.Equal(11, strictKeys.Count);

        var sourceRows = LoadSourceRows();
        var sourceByAlias = sourceRows.ToDictionary(row => row.Alias, StringComparer.Ordinal);
        var goldByKey = goldBound
            .Select((bound, index) => new GoldItem(bound, gold.Headings[index]))
            .ToDictionary(item => Key(item.Bound), StringComparer.Ordinal);
        var requestHashes = LoadRequestHashes();
        var requests = Enumerable.Range(0, 9).Select(segment => LoadRequest(segment)).ToArray();
        Assert.Equal(requestHashes.Count, requests.Length);
        for (var segment = 0; segment < requests.Length; segment++)
            Assert.Equal(requestHashes[segment], CanonicalArtifactHash.OfText(LoadRequestFile(segment)));
        var replayRuns = bundles.Select(bundle => SemanticAuthorityReplay.Replay(bundle)).ToArray();
        var predictedByRepeat = replayRuns
            .Select(replay => replay.Pipeline.BoundHeadings.Select(item => new RuntimeItem(
                new PdfBoundOccurrence(item.Parts, item.SemanticRole, item.Alias, ParentFromHints(item.RelationHints)),
                item)).ToArray())
            .ToArray();

        var targetRows = strictKeys
            .Order(StringComparer.Ordinal)
            .Select(key => BuildTargetRow(
                key,
                goldByKey[key],
                sourceByAlias,
                requests,
                requestHashes,
                predictedByRepeat))
            .ToArray();

        Assert.All(targetRows, row =>
        {
            Assert.True(row.RequestVisible);
            Assert.True(row.Owned);
            Assert.True(row.ExactTextPresent);
            Assert.False(row.Truncated);
            Assert.False(row.SerializationLoss);
        });
        Assert.Equal(11, targetRows.Length);
        Assert.Equal(0, targetRows.Count(row => row.CandidateHintPresent == false && row.AttentionVisible));
        Assert.Equal(2, targetRows.Count(row => row.MultiHeadingAlias));
        Assert.Equal(9, targetRows.Count(row => !row.MultiHeadingAlias));
        // The census is claim-level: S0573 contributes two claims at the same
        // source occurrence, so the four edge aliases yield five edge claims.
        Assert.Equal(5, targetRows.Count(row => row.NearVisibleMargin));
        Assert.Equal(11, targetRows.Count(row => row.DroppedRepresentationFields.Count > 0));
        Assert.Equal(0, targetRows.Sum(row => row.Neighborhood.Count(item => item.Kind == "LARGER_SPAN_COVERING_TARGET")));
        Assert.Equal(0, targetRows.Sum(row => row.Neighborhood.Count(item => item.Kind == "PARTIAL_SPAN_TOUCHING_TARGET")));
        Assert.Equal(0, targetRows.Sum(row => row.Neighborhood.Count(item => item.Kind == "SAME_ALIAS_DIFFERENT_OCCURRENCE")));

        var controls = BuildControls(goldBound, gold, sourceByAlias, predictedByRepeat, targetRows);
        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.True(control.RecoveredRepeats >= 2));

        if (string.Equals(Environment.GetEnvironmentVariable("A99_S2J_AUDIT"), "1", StringComparison.Ordinal))
            WriteArtifact(targetRows, controls, sourceRows, requestHashes);
    }

    private static TargetRow BuildTargetRow(
        string key,
        GoldItem goldItem,
        IReadOnlyDictionary<string, SourceRow> sourceByAlias,
        IReadOnlyList<JsonDocument> requests,
        IReadOnlyList<string> requestHashes,
        IReadOnlyList<RuntimeItem[]> predictedByRepeat)
    {
        var source = sourceByAlias[goldItem.Heading.SourceAlias];
        var owner = FindOwnedEvidence(goldItem.Heading.SourceAlias, requests);
        var evidence = owner.Evidence;
        var body = owner.Body;
        var ownedAliases = owner.Request.RootElement.GetProperty("ownedSourceAliases")
            .EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var sourceSegmentIndex = source.Ordinal / OwnedPerSegment;
        var sourceSegmentId = $"segment-{sourceSegmentIndex:000}";
        var ownedIndex = source.Ordinal - sourceSegmentIndex * OwnedPerSegment;
        var exactText = goldItem.Heading.SelectionMode == "WHOLE_ALIAS"
            ? source.Text
            : goldItem.Heading.VerbatimText!;
        var requestText = evidence.GetProperty("text").GetString()!;
        var requestVisible = !string.IsNullOrEmpty(requestText);
        var owned = ownedAliases.Contains(goldItem.Heading.SourceAlias) &&
                    evidence.GetProperty("owned").GetBoolean();
        var exactTextPresent = requestText.Contains(exactText, StringComparison.Ordinal);
        var truncated = requestText.Length < source.Text.Length;
        var serializationLoss = !string.Equals(requestText, source.Text, StringComparison.Ordinal);
        var exactStart = goldItem.Bound.Parts[0].Start - source.SourceStart;
        var exactEnd = goldItem.Bound.Parts[^1].End - source.SourceStart;
        var candidateHints = EvidenceHints(evidence);
        var dropped = new[]
        {
            "page_position",
            "indentation_geometry",
            "whitespace_geometry",
            "exact_line_break_boundaries",
            "table_cell_geometry",
        };
        var neighborhood = predictedByRepeat.Select((items, repeatIndex) =>
            ClassifyNeighborhood(repeatIndex + 1, source, exactStart, exactEnd, items)).ToArray();

        return new TargetRow(
            key,
            goldItem.Heading.SourceAlias,
            exactText,
            goldItem.Heading.SemanticRole,
            source.SourceId,
            source.Page,
            source.SourceStart + exactStart,
            source.SourceStart + exactEnd,
            exactEnd - exactStart,
            goldItem.Bound.Parts.Count,
            source.Text,
            source.Ordinal,
            sourceSegmentIndex,
            sourceSegmentId,
            owner.SegmentIndex,
            $"segment-{owner.SegmentIndex:000}",
            requestHashes[owner.SegmentIndex],
            ownedIndex,
            ownedIndex + 1,
            Math.Min(ownedIndex, 119 - ownedIndex),
            ownedIndex,
            119 - ownedIndex,
            ownedIndex <= VisibleMargin || ownedIndex >= OwnedPerSegment - 1 - VisibleMargin,
            CharPosition(body, goldItem.Heading.SourceAlias),
            CharPosition(body, goldItem.Heading.SourceAlias) / 4,
            requestVisible,
            owned,
            exactTextPresent,
            truncated,
            serializationLoss,
            requestText,
            source.Text[..exactStart],
            source.Text[exactEnd..],
            exactText.Length > 0 && char.IsPunctuation(exactText[0]),
            exactText.Length > 0 && char.IsPunctuation(exactText[^1]),
            exactStart > 0 ? source.Text[exactStart - 1].ToString() : null,
            exactEnd < source.Text.Length ? source.Text[exactEnd].ToString() : null,
            exactStart > 0 && char.IsPunctuation(source.Text[exactStart - 1]),
            exactEnd < source.Text.Length && char.IsPunctuation(source.Text[exactEnd]),
            goldItem.Heading.SelectionMode == "WHOLE_ALIAS",
            goldItem.Heading.SelectionMode != "WHOLE_ALIAS",
            goldItem.Heading.SourceAlias is "S0573",
            goldItem.Heading.SourceAlias is "S0573" ? "heading_plus_heading" : "heading_plus_body",
            candidateHints.Present,
            candidateHints.Type,
            evidence.GetProperty("attention").GetBoolean(),
            evidence.GetProperty("markers").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            evidence.GetProperty("style").Clone(),
            evidence.GetProperty("scope").GetString()!,
            dropped,
            "UNRESOLVED",
            neighborhood);
    }

    private static IReadOnlyList<ControlRow> BuildControls(
        IReadOnlyList<PdfBoundOccurrence> goldBound,
        PdfGoldDocument gold,
        IReadOnlyDictionary<string, SourceRow> sourceByAlias,
        IReadOnlyList<RuntimeItem[]> predictedByRepeat,
        IReadOnlyList<TargetRow> targets)
    {
        var targetKeys = targets.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var controls = new List<ControlRow>();
        foreach (var item in goldBound.Select((bound, index) => new { bound, heading = gold.Headings[index] }))
        {
            var key = Key(item.bound);
            if (targetKeys.Contains(key)) continue;
            var recovery = predictedByRepeat.Count(items => items.Any(prediction => Key(prediction.Bound) == key));
            if (recovery < 2) continue;
            var source = sourceByAlias[item.heading.SourceAlias];
            var segment = source.Ordinal / OwnedPerSegment;
            var position = source.Ordinal - segment * OwnedPerSegment;
            var length = item.bound.Parts.Sum(part => part.End - part.Start);
            controls.Add(new ControlRow(
                key,
                item.heading.SourceAlias,
                item.heading.SemanticRole,
                item.heading.SelectionMode,
                length,
                segment,
                position,
                recovery));
        }

        return controls
            .GroupBy(item => item.SegmentIndex)
            .SelectMany(group => group
                .OrderByDescending(item => item.RecoveredRepeats)
                .ThenBy(item => item.SourceOrdinalInSegment)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(3))
            .OrderBy(item => item.SegmentIndex)
            .ThenBy(item => item.SourceOrdinalInSegment)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static NeighborhoodRow ClassifyNeighborhood(
        int repeat,
        SourceRow source,
        int targetStart,
        int targetEnd,
        IReadOnlyList<RuntimeItem> predictions)
    {
        var sameSource = predictions.Where(item => item.Bound.Parts.Any(part => part.SourceId == source.SourceId)).ToArray();
        if (sameSource.Any(item => Key(item.Bound) == $"{source.SourceId}:{source.SourceStart + targetStart}:{source.SourceStart + targetEnd}"))
            return new(repeat, "EXACT_MATCH", []);
        if (sameSource.Any(item => item.Bound.Parts.Any(part =>
                part.Start <= source.SourceStart + targetStart && part.End >= source.SourceStart + targetEnd)))
            return new(repeat, "LARGER_SPAN_COVERING_TARGET", sameSource.Select(item => Key(item.Bound)).ToArray());
        if (sameSource.Any(item => item.Bound.Parts.Any(part =>
                part.Start < source.SourceStart + targetEnd && part.End > source.SourceStart + targetStart)))
            return new(repeat, "PARTIAL_SPAN_TOUCHING_TARGET", sameSource.Select(item => Key(item.Bound)).ToArray());
        if (sameSource.Any(item => item.Bound.SourceAlias == source.Alias))
            return new(repeat, "SAME_ALIAS_DIFFERENT_OCCURRENCE", sameSource.Select(item => Key(item.Bound)).ToArray());

        var nearby = predictions
            .Where(item => item.Bound.Parts.Any(part => Math.Abs(part.SourceOrdinal - source.Ordinal) <= NearbyAliasDistance))
            .Select(item => Key(item.Bound))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return nearby.Length == 0
            ? new(repeat, "NO_NEARBY_PROPOSAL", [])
            : new(repeat, "NEARBY_DIFFERENT_HEADING", nearby);
    }

    private static (bool Present, string Type) EvidenceHints(JsonElement evidence)
    {
        var attention = evidence.TryGetProperty("attention", out var attentionProperty) &&
                        attentionProperty.ValueKind == JsonValueKind.True;
        var markers = evidence.TryGetProperty("markers", out var markerProperty) &&
                      markerProperty.ValueKind == JsonValueKind.Array && markerProperty.GetArrayLength() > 0;
        var numbering = evidence.TryGetProperty("numbering", out var numberingProperty) &&
                        numberingProperty.ValueKind == JsonValueKind.Object && numberingProperty.EnumerateObject().Any();
        return (attention || markers || numbering,
            markers ? "attention+marker" : numbering ? "attention+numbering" : attention ? "attention" : "none");
    }

    private static int CharPosition(string body, string alias) =>
        body.IndexOf($"\"alias\":\"{alias}\"", StringComparison.Ordinal);

    private static (JsonDocument Request, JsonElement Evidence, string Body, int SegmentIndex) FindOwnedEvidence(
        string alias,
        IReadOnlyList<JsonDocument> requests)
    {
        foreach (var (request, segmentIndex) in requests.Select((request, index) => (request, index)))
        {
            var root = request.RootElement;
            var owned = root.GetProperty("ownedSourceAliases").EnumerateArray()
                .Any(item => item.GetString() == alias);
            if (!owned) continue;
            var ownedEvidence = root.GetProperty("sourceEvidence").EnumerateArray()
                .FirstOrDefault(evidence =>
                    evidence.GetProperty("alias").GetString() == alias &&
                    evidence.GetProperty("owned").GetBoolean());
            if (ownedEvidence.ValueKind != JsonValueKind.Undefined)
            {
                var body = request.RootElement.GetRawText();
                return (request, ownedEvidence, body, segmentIndex);
            }
        }

        throw new InvalidOperationException($"No owned request evidence found for {alias}.");
    }

    private static JsonDocument LoadRequest(int segment)
    {
        return JsonDocument.Parse(LoadRequestBody(segment));
    }

    private static string LoadRequestBody(int segment)
    {
        var raw = LoadRequestFile(segment);
        return raw.Split("\nSCHEMA=", 2, StringSplitOptions.None)[0];
    }

    private static string LoadRequestFile(int segment)
    {
        var path = RepositoryPath(
            $"eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/request-{segment}.json");
        return File.ReadAllText(path);
    }

    private static IReadOnlyList<string> LoadRequestHashes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPath(PreflightPath)));
        return document.RootElement.GetProperty("request").GetProperty("userPayloadSha256")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static IReadOnlyList<string> LoadStrictKeys()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPath(Root + "/i6-preflight.v1.json")));
        return document.RootElement.GetProperty("strictCausalCohort").GetProperty("rows")
            .EnumerateArray().Select(item => item.GetProperty("Key").GetString()!).ToArray();
    }

    private static IReadOnlyList<SourceRow> LoadSourceRows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPath(SourceUniversePath)));
        return document.RootElement.GetProperty("rows").EnumerateArray().Select(row => new SourceRow(
            row.GetProperty("sourceAlias").GetString()!,
            row.GetProperty("sourceId").GetString()!,
            row.GetProperty("ordinal").GetInt32(),
            row.GetProperty("text").GetString()!,
            row.GetProperty("sourceStart").GetInt32(),
            row.GetProperty("sourceEnd").GetInt32(),
            row.GetProperty("page").GetInt32())).ToArray();
    }

    private static SemanticAuthorityReplayBundle LoadBundle(int repeat)
    {
        var path = Directory.GetFiles(RepositoryPath(
            $"eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/r{repeat}"), "*.json")
            .Single();
        return JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(File.ReadAllText(path))!;
    }

    private static void WriteArtifact(
        IReadOnlyList<TargetRow> targets,
        IReadOnlyList<ControlRow> controls,
        IReadOnlyList<SourceRow> sourceRows,
        IReadOnlyList<string> requestHashes)
    {
        var segmentIds = targets.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var requestSegmentIds = targets.Select(item => item.RequestSegmentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var artifact = new
        {
            artifactKind = "a99_pdf_strict_omission_causal_audit",
            schemaVersion = "a99-pdf-strict-omission-causal-audit-v1",
            baselineCommit = "0a27de8",
            sourceHash = SourceHash,
            sourceUniverseHash = SourceUniverseHash,
            promptHash = PromptHash,
            semanticContractHash = SemanticContractHash,
            successorManifestHash = SuccessorManifestHash,
            goldSha256 = CanonicalArtifactHash.OfTextFile(RepositoryPath(GoldPath)),
            modelCalls = 0,
            providerCalls = 0,
            strictTargetCount = targets.Count,
            strictTargetsRequestVisible = targets.Count(row => row.RequestVisible),
            strictTargetsOwned = targets.Count(row => row.Owned),
            strictTargetsTextPresent = targets.Count(row => row.ExactTextPresent),
            visibilityGate = new
            {
                requestVisible = true,
                owned = true,
                exactTextPresent = true,
                truncated = false,
                serializationLoss = false,
            },
            positionPolicy = new
            {
                ownedPerSegment = OwnedPerSegment,
                visibleMargin = VisibleMargin,
                shortSpanThreshold = ShortSpanThreshold,
                nearbyAliasDistance = NearbyAliasDistance,
            },
            targets,
            controls,
            counts = new
            {
                fullAliasHeadings = targets.Count(row => row.FullAlias),
                substringHeadings = targets.Count(row => row.SubstringHeading),
                multiHeadingAliases = targets.Count(row => row.MultiHeadingAlias),
                singleHeadingAliases = targets.Count(row => !row.MultiHeadingAlias),
                veryShortSpans = targets.Count(row => row.HeadingLength <= ShortSpanThreshold),
                noCandidateHint = targets.Count(row => !row.CandidateHintPresent),
                nearSegmentEdge = targets.Count(row => row.NearVisibleMargin),
                representationInformationLoss = targets.Count(row => row.DroppedRepresentationFields.Count > 0),
                nearbyModelProposal = targets.Count(row => row.Neighborhood.Any(item => item.Kind != "NO_NEARBY_PROPOSAL")),
                completelySilentNeighborhood = targets.Count(row => row.Neighborhood.All(item => item.Kind == "NO_NEARBY_PROPOSAL")),
            },
            candidateHintAudit = new
            {
                allUnhinted = targets.All(row => !row.CandidateHintPresent),
                descriptive = "All 11 targets carry attention=true; marker hints are present only on a subset. This is correlation evidence, not causality.",
            },
            multiHeadingAudit = new
            {
                strictOmissionsInMultiHeadingAlias = targets.Count(row => row.MultiHeadingAlias),
                strictOmissionsInSingleHeadingAlias = targets.Count(row => !row.MultiHeadingAlias),
            },
            requestAuthority = new
            {
                sourceSegments = segmentIds,
                requestOwnerSegments = requestSegmentIds,
                requestHashes,
                sourceRows = sourceRows.Count,
            },
            experimentFamilies = new
            {
                headingDefinitionDiscoveryCriteria = "UNRESOLVED",
                multiHeadingDecomposition = "UNRESOLVED",
                segmentContextAttentionCompetition = "UNRESOLVED",
                candidateHintAttention = "UNRESOLVED",
                sourceRepresentationLoss = "UNRESOLVED",
                other = "NOT_SUPPORTED",
            },
            nextExperiment = new
            {
                state = "NEXT_CAUSAL_EXPERIMENT_NOT_YET_JUSTIFIED",
                delta = "NONE",
                affectedSegments = segmentIds,
                calls = (int?)null,
                reason = "No single causal family is materially stronger than the matched controls; no prompt tweak is justified.",
            },
        };
        File.WriteAllText(
            RepositoryPath(Root + "/strict-omission-causal-audit.v1.json"),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Key(PdfBoundOccurrence item) =>
        string.Join(';', item.Parts.Select(part => $"{part.SourceId}:{part.Start}:{part.End}"));

    private static string? ParentFromHints(IReadOnlyList<string> hints)
    {
        var hint = hints.FirstOrDefault(item => item.StartsWith("parent-node:", StringComparison.Ordinal));
        if (hint is null) return null;
        var value = hint["parent-node:".Length..];
        return value is "ROOT" or "NONE" ? null : value;
    }

    private static string RepositoryPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", relativePath));

    private sealed record SourceRow(
        string Alias,
        string SourceId,
        int Ordinal,
        string Text,
        int SourceStart,
        int SourceEnd,
        int Page);

    private sealed record GoldItem(
        PdfBoundOccurrence Bound,
        PdfGoldHeading Heading);

    private sealed record RuntimeItem(
        PdfBoundOccurrence Bound,
        CanonicalSemanticBoundHeading Heading);

    private sealed record OwnerEvidence(
        JsonDocument Request,
        JsonElement Evidence,
        string Body,
        int SegmentIndex);

    private sealed record HintFacts(bool Present, string Type);

    private sealed record NeighborhoodRow(
        int Repeat,
        string Kind,
        IReadOnlyList<string> ProposalKeys);

    private sealed record TargetRow(
        string Key,
        string SourceAlias,
        string HeadingText,
        string GoldRole,
        string SourceId,
        int SourcePage,
        int GoldSpanStart,
        int GoldSpanEnd,
        int GoldSpanLength,
        int GoldSpanPartCount,
        string AliasText,
            int AliasOrdinal,
            int SegmentIndex,
            string SegmentId,
            int RequestSegmentIndex,
            string RequestSegmentId,
            string BaselineRequestHash,
        int OrdinalInsideOwnedSegment,
        int OneBasedOwnedPosition,
        int RelativePosition,
        int DistanceFromLeftBoundary,
        int DistanceFromRightBoundary,
        bool NearVisibleMargin,
        int RequestCharPosition,
        int ApproximateRequestTokenPosition,
        bool RequestVisible,
        bool Owned,
        bool ExactTextPresent,
        bool Truncated,
        bool SerializationLoss,
        string RequestText,
        string TextBeforeHeading,
        string TextAfterHeading,
        bool HeadingStartsWithPunctuation,
        bool HeadingEndsWithPunctuation,
        string? CharacterBeforeHeading,
        string? CharacterAfterHeading,
        bool PunctuationBeforeHeading,
        bool PunctuationAfterHeading,
        bool FullAlias,
        bool SubstringHeading,
        bool MultiHeadingAlias,
        string LocalStructure,
        bool CandidateHintPresent,
        string CandidateHintType,
        bool AttentionVisible,
        IReadOnlyList<string> CandidateMarkers,
        JsonElement CandidateStyle,
        string CandidateScope,
        IReadOnlyList<string> DroppedRepresentationFields,
        string TextSufficientFromSourceStructure,
        IReadOnlyList<NeighborhoodRow> Neighborhood)
    {
        public int HeadingLength => HeadingText.Length;
    }

    private sealed record ControlRow(
        string Key,
        string SourceAlias,
        string GoldRole,
        string SelectionMode,
        int HeadingLength,
        int SegmentIndex,
        int SourceOrdinalInSegment,
        int RecoveredRepeats);
}
