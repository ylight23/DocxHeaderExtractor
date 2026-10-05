using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes the clean V1/V2 semantic-boundary pair before any provider execution.</summary>
public sealed class V5P6TH2CCleanPairedPreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string Input = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string Output = Root + "/p6th2c-clean-v1-v2-preflight/h2c-clean-v1-v2-preflight.v1.json";
    private const string V1 = """
        Determine the exact source extent of the one heading that begins at the issued anchor occurrence. A heading may consist of one or more consecutive source occurrences. Return every and only consecutive occurrence that belongs literally to this exact heading. Do not include later body content merely because it belongs to the same section, topic, agenda item, or semantic region.
        """;
    private const string V2 = """
        Locate the exact boundary of the single heading occurrence that begins at the issued anchor occurrence. A heading may contain one or more consecutive source occurrences, but it ends immediately before the first occurrence that is not literally part of that same heading occurrence.

        Treat an occurrence as outside the heading when it begins a new heading, starts body or prose content, starts a table or other structured content, is page furniture, or otherwise is not literal heading text. Do not extend the heading merely because a later occurrence belongs to the same section, topic, agenda item, document region, or discusses the same subject.

        Choose the last literal heading occurrence and its immediate successor as one boundary pair: headingMembers must end at endOccurrence, and firstOutsideOccurrence must be the next issued occurrence immediately after it.
        """;
    private const string Shared = """
        For each request, copy the anchor value exactly from that request's input anchor field. Never substitute an identifier from instructions, prior requests, or another occurrence. Return exactly one decision for that anchor with these five properties: anchor, headingMembers, endOccurrence, firstOutsideOccurrence, and firstOutsideRole. headingMembers must begin with that exact anchor value and be one contiguous prefix of the ordered issued occurrences. endOccurrence must equal its final member. firstOutsideOccurrence must be the immediate successor after endOccurrence, never a skipped occurrence. If every issued occurrence belongs to the heading and there is no visible successor, use null for firstOutsideOccurrence and NO_VISIBLE_SUCCESSOR for firstOutsideRole.

        When firstOutsideOccurrence is present, firstOutsideRole must be exactly one of NEW_HEADING, BODY_CONTENT, PAGE_FURNITURE, TABLE_OR_STRUCTURED_CONTENT, OTHER_NON_HEADING. These are descriptive roles of the first occurrence outside the exact heading, not permission to extend the heading. Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.

        Return one JSON object only with root property decisions and exactly one decision per input anchor. Each decision must have exactly the five required properties and no others. Copy only issued occurrence handles from the current request. Do not output source text or additional properties. This contract has no example identifiers; use the actual anchor and occurrence handles present in the current request.
        """;

    [Fact]
    public void Clean_V1_V2_pair_freezes_same_G2A_Has_cohort_and_only_semantic_boundary_delta()
    {
        using var input = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Input)));
        var root = input.RootElement;
        var universe = root.GetProperty("requestUniverse").EnumerateArray().ToArray();
        Assert.Equal(31, universe.Length);
        Assert.Equal(31, universe.Select(x => $"{x.GetProperty("DocumentId").GetString()}|{x.GetProperty("PackId").GetString()}|{x.GetProperty("Anchor").GetString()}").Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("O17", V1 + V2 + Shared, StringComparison.Ordinal);

        var v1Prompt = V1 + "\n\n" + Shared;
        var v2Prompt = V2 + "\n\n" + Shared;
        Assert.NotEqual(v1Prompt, v2Prompt);
        Assert.Contains(Shared, v1Prompt, StringComparison.Ordinal);
        Assert.Contains(Shared, v2Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Gold", v1Prompt + v2Prompt, StringComparison.OrdinalIgnoreCase);

        var rows = universe.Select((row, index) => new
        {
            providerCallOrdinal = index + 1,
            documentId = row.GetProperty("DocumentId").GetString(),
            packId = row.GetProperty("PackId").GetString(),
            anchor = row.GetProperty("Anchor").GetString(),
            anchorAlias = row.GetProperty("AnchorAlias").GetString(),
            issuedOccurrenceCount = row.GetProperty("IssuedOccurrenceCount").GetInt32(),
            issuedOccurrencesSha256 = Hash(string.Join("\n", row.GetProperty("IssuedOccurrences").EnumerateArray().Select(x => x.GetString())) + "\n"),
            v1 = new { systemPromptSha256 = Hash(v1Prompt), userMessageSha256 = row.GetProperty("UserMessageSha256").GetString(), sourceFacts = "SHARED_FROZEN_V2_REQUEST_UNIVERSE" },
            v2 = new { systemPromptSha256 = Hash(v2Prompt), userMessageSha256 = row.GetProperty("UserMessageSha256").GetString(), sourceFacts = "SHARED_FROZEN_V2_REQUEST_UNIVERSE" },
        }).ToArray();

        FreezeArtifact.AssertJson(Root + "/p6th2c-clean-v1-v2-preflight", "h2c-clean-v1-v2-preflight.v1.json", new
        {
            schemaVersion = "v5-p6th2c-clean-v1-v2-preflight-v1",
            status = "PROVIDER_NOT_RUN_GOLD_NOT_READ_COHORT_FROZEN",
            treatment = new
            {
                model = "qwen/qwen3.7-flash", provider = "alibaba", temperature = 0,
                reasoning = new { enabled = true, effort = "OMITTED" },
                sourceCohort = "FROZEN_G2A_HAS_REQUEST_UNIVERSE",
                onlySemanticVariable = true,
                sharedSchema = "H2C_EXACT_END_POINTER_V1",
                sharedAnchorCopyRule = true,
                sharedSourceFacts = true,
                sharedHorizon = true,
                v1SemanticPromptSha256 = Hash(v1Prompt),
                v2SemanticPromptSha256 = Hash(v2Prompt),
            },
            authority = new { inputManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(Input))), goldRead = false, providerCalls = 0, retries = 0, repair = false, fallback = false, runtimeChanged = false },
            requestCount = rows.Length,
            requests = rows,
        });
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
