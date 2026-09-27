using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// An offline design freeze, not a transport implementation.  It makes the proposed V4 decision
/// shape executable enough to reject ambiguous authorities before an experimental request is ever
/// created or a provider is called.
/// </summary>
public sealed class SemanticFunctionContractDesignFreezeTests
{
    private const string Dir = "eval/a99-closed-loop/semantic-function-contract-design-v1";
    private const string V2Run = "eval/a99-closed-loop/llm-semantic-pilot-v1/run.v1.json";

    private static readonly string[] Functions =
    [
        "DOCUMENT_IDENTITY",
        "REGION_STRUCTURE",
        "NAVIGATION",
        "PAGE_FURNITURE",
        "OBJECT_CAPTION",
        "TABLE_STRUCTURE",
        "FOOTNOTE_OR_SOURCE",
        "BODY_INFORMATION",
        "METADATA",
    ];

    private static readonly HashSet<string> FunctionSet = new(Functions, StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedFields = new(
        ["sourceParts", "semanticFunction", "occurrenceRole", "scope", "titleRelation"], StringComparer.Ordinal);

    private sealed record Decision(
        string SemanticFunction,
        bool IsMember,
        string SourcePartsJson,
        string? OccurrenceRole,
        string? Scope,
        string? TitleRelation);

    private sealed record V2BindingInvariant(string SourcePartsSha256, string BoundIdentity);

    private static readonly Lazy<V2BindingInvariant> RawV2Binding = new(ReadOneBoundV2Coordinate);

    [Fact]
    public void The_design_schema_has_one_closed_membership_authority()
    {
        var schema = JsonSerializer.Serialize(ProposedSchema());

        Assert.DoesNotContain("isHeading", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("semanticRole", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"heading\"", schema, StringComparison.Ordinal);
        foreach (var function in Functions) Assert.Contains(function, schema, StringComparison.Ordinal);
        Assert.Equal(9, Functions.Length);
    }

    [Fact]
    public void Unknown_or_generic_semantic_function_fails_closed()
    {
        foreach (var function in new[] { "heading", "SECTION", "REGION_STRUCTURE ", "qqq-not-a-role-42" })
        {
            using var entry = JsonDocument.Parse($$"""{"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"{{function}}"}""");
            Assert.False(TryDecode(entry.RootElement, out _, out var reason));
            Assert.Equal("UNKNOWN_SEMANTIC_FUNCTION", reason);
        }
    }

    [Fact]
    public void Membership_is_derived_only_from_semantic_function()
    {
        foreach (var function in Functions)
        {
            using var entry = JsonDocument.Parse($$"""
                {"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"{{function}}",
                 "occurrenceRole":"section-heading","scope":"document","titleRelation":"root"}
                """);
            Assert.True(TryDecode(entry.RootElement, out var decision, out var reason), reason);
            Assert.Equal(function is "DOCUMENT_IDENTITY" or "REGION_STRUCTURE", decision!.IsMember);
        }

        using var forbiddenOverride = JsonDocument.Parse("""
            {"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"NAVIGATION","isHeading":true}
            """);
        Assert.False(TryDecode(forbiddenOverride.RootElement, out _, out var issue));
        Assert.Equal("FIELD_NOT_IN_CONTRACT:isHeading", issue);
    }

    [Fact]
    public void Navigation_furniture_caption_table_note_body_and_metadata_can_never_be_members()
    {
        foreach (var function in new[]
        {
            "NAVIGATION", "PAGE_FURNITURE", "OBJECT_CAPTION", "TABLE_STRUCTURE", "FOOTNOTE_OR_SOURCE", "BODY_INFORMATION", "METADATA",
        })
        {
            using var entry = JsonDocument.Parse($$"""{"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"{{function}}"}""");
            Assert.True(TryDecode(entry.RootElement, out var decision, out var reason), reason);
            Assert.False(decision!.IsMember, function);
        }
    }

    [Fact]
    public void The_same_words_can_have_different_decisions_for_different_occurrences()
    {
        const string text = "3.2 Server Push";
        using var contents = JsonDocument.Parse("""
            {"sourceParts":[{"sourceAlias":"L0012:S0","verbatimText":"3.2 Server Push ........ 17"}],
             "semanticFunction":"NAVIGATION","occurrenceRole":"toc-entry","scope":"contents"}
            """);
        using var body = JsonDocument.Parse("""
            {"sourceParts":[{"sourceAlias":"L0517:S0","verbatimText":"3.2 Server Push"}],
             "semanticFunction":"REGION_STRUCTURE","occurrenceRole":"section","scope":"body"}
            """);

        Assert.True(TryDecode(contents.RootElement, out var navigation, out var navigationReason), navigationReason);
        Assert.True(TryDecode(body.RootElement, out var region, out var regionReason), regionReason);
        Assert.False(navigation!.IsMember);
        Assert.True(region!.IsMember);
        Assert.NotEqual(navigation.SourcePartsJson, region.SourcePartsJson);
        Assert.Contains(text, navigation.SourcePartsJson, StringComparison.Ordinal);
        Assert.Contains(text, region.SourcePartsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_parts_and_the_existing_binder_are_unchanged_and_v2_stays_immutable()
    {
        // A proposed decision transports the original coordinate tuple verbatim; it has no authority
        // to edit a source part or change how the existing binder computes the identity.
        var raw = RawV2Binding.Value;
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V2Run)));
        var entry = FirstBoundV2Entry(run.RootElement, out var originalBinding);
        var sourcePartsJson = entry.GetProperty("sourceParts").GetRawText();
        using var proposed = JsonDocument.Parse($$"""{"sourceParts":{{sourcePartsJson}},"semanticFunction":"REGION_STRUCTURE"}""");
        Assert.True(TryDecode(proposed.RootElement, out var decision, out var reason), reason);
        Assert.Equal(sourcePartsJson, decision!.SourcePartsJson);
        Assert.Equal(raw.BoundIdentity, originalBinding.Identity);
        Assert.Equal(raw.SourcePartsSha256, Sha(sourcePartsJson));

        // This study deliberately adds no enum value, request builder, production schema, or V2 edit.
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
        Assert.DoesNotContain(Enum.GetNames<SemanticRequestVersion>(), value => value.StartsWith("V4", StringComparison.Ordinal));
        Assert.Contains("isHeading", JsonSerializer.Serialize(SemanticSourcePartsContract.Schema()), StringComparison.Ordinal);
        Assert.True(File.Exists(TestRepository.Path(V2Run)));
    }

    [Fact]
    public void Freeze_the_contract_design_before_any_provider_arm()
    {
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_PILOT_RUN") is "1" or "true" or "TRUE");
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_ARM_RUN") is "1" or "true" or "TRUE");

        FreezeArtifact.AssertJson(Dir, "contract-freeze.v1.json", new
        {
            artifactKind = "a99_semantic_function_contract_design_freeze",
            study = "SEMANTIC_FUNCTION_CONTRACT_DESIGN_FREEZE_V1",
            status = "offline_design_only_not_a_production_protocol",
            modelProviderCalls = 0,
            follows = new
            {
                replay = "docs/accuracy/semantic-function-membership-replay-v1.md",
                conclusion = "single authority removes contradictions, but legacy semanticRole projection cannot distinguish navigation from region structure",
            },
            primarySemanticFunction = new
            {
                soleMembershipAuthority = true,
                closedVocabulary = Functions,
                membership = new
                {
                    members = new[] { "DOCUMENT_IDENTITY", "REGION_STRUCTURE" },
                    nonMembers = Functions.Except(["DOCUMENT_IDENTITY", "REGION_STRUCTURE"], StringComparer.Ordinal).ToArray(),
                    unknownFailsClosed = true,
                },
                definitions = new
                {
                    regionStructure = "an occurrence that names or opens a semantic region whose subsequent content belongs beneath it",
                    navigation = "an occurrence that points to, lists, or leads to content elsewhere",
                    occurrenceNotString = "identical or overlapping text may have different functions at different source occurrences and contexts",
                },
            },
            descriptiveAxes = new
            {
                allowed = new[] { "occurrenceRole", "scope", "titleRelation" },
                membershipAuthority = false,
                cannotOverrideSemanticFunction = true,
            },
            schema = ProposedSchema(),
            deterministicProofs = new[]
            {
                "isHeading is absent and rejected as a field not in the contract",
                "generic heading is not an enum value and fails closed",
                "unknown semanticFunction fails closed",
                "membership is derived only from semanticFunction",
                "NAVIGATION, PAGE_FURNITURE, OBJECT_CAPTION, TABLE_STRUCTURE, FOOTNOTE_OR_SOURCE, BODY_INFORMATION and METADATA are nonmembers",
                "the same words can be NAVIGATION in contents and REGION_STRUCTURE in body",
                "sourceParts are preserved verbatim and the existing binder identity is unchanged",
                "V2 remains production default and its committed raw run remains a historical input",
            },
            v2Immutability = new
            {
                productionDefault = SemanticRequestVersions.ProductionDefault.ToString(),
                requestVersions = Enum.GetNames<SemanticRequestVersion>(),
                structuredV1SchemaHash = SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash(),
                rawRun = new { path = V2Run, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(V2Run)) },
                v2CoordinatePreservedThroughDesign = RawV2Binding.Value,
            },
            nextGate = "Do not create or send V4 until this frozen decision shape is reviewed; a future arm must use this closed vocabulary rather than project legacy semanticRole.",
        });
    }

    private static object ProposedSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            sourceParts = new
            {
                type = "array",
                minItems = 1,
                description = "The existing source-coordinate tuple; its binder and canonicalization are unchanged by this design.",
            },
            semanticFunction = new
            {
                type = "string",
                @enum = Functions,
                description = "The sole authority from which membership is derived.",
            },
            occurrenceRole = new { type = "string", description = "Optional descriptive axis; never a membership authority." },
            scope = new { type = "string", description = "Optional descriptive axis; never a membership authority." },
            titleRelation = new { type = "string", description = "Optional descriptive axis; never a membership authority." },
        },
        required = new[] { "sourceParts", "semanticFunction" },
    };

    private static bool TryDecode(JsonElement entry, out Decision? decision, out string? reason)
    {
        decision = null;
        reason = null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            reason = "ENTRY_NOT_OBJECT";
            return false;
        }
        foreach (var property in entry.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
            {
                reason = $"FIELD_NOT_IN_CONTRACT:{property.Name}";
                return false;
            }
        }
        if (!entry.TryGetProperty("semanticFunction", out var function) || function.ValueKind != JsonValueKind.String ||
            !FunctionSet.Contains(function.GetString()!))
        {
            reason = "UNKNOWN_SEMANTIC_FUNCTION";
            return false;
        }
        if (!entry.TryGetProperty("sourceParts", out var sourceParts) || sourceParts.ValueKind != JsonValueKind.Array ||
            sourceParts.GetArrayLength() == 0)
        {
            reason = "SOURCE_PARTS_REQUIRED";
            return false;
        }

        var value = function.GetString()!;
        decision = new Decision(value, value is "DOCUMENT_IDENTITY" or "REGION_STRUCTURE", sourceParts.GetRawText(),
            OptionalString(entry, "occurrenceRole"), OptionalString(entry, "scope"), OptionalString(entry, "titleRelation"));
        return true;
    }

    private static string? OptionalString(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static V2BindingInvariant ReadOneBoundV2Coordinate()
    {
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V2Run)));
        var entry = FirstBoundV2Entry(run.RootElement, out var binding);
        var sourceParts = entry.GetProperty("sourceParts").GetRawText();
        return new V2BindingInvariant(Sha(sourceParts), binding.Identity);
    }

    private static JsonElement FirstBoundV2Entry(JsonElement run, out SemanticSourcePartsBinding binding)
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(
            TestRepository.Path(Src089BlindGeneralizationTests.Pdf), PdfSourceFactsVersion.V3_RobustGlyphStatistics).Atoms;
        foreach (var call in run.GetProperty("ledger").EnumerateArray()
                     .Where(item => item.GetProperty("DocumentId").GetString() == "SRC-089"))
        {
            var responseText = call.GetProperty("Response").GetString();
            if (responseText is null) continue;
            using var response = JsonDocument.Parse(responseText);
            if (!response.RootElement.TryGetProperty("headings", out var headings)) continue;
            foreach (var entry in headings.EnumerateArray())
            {
                var decoded = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(entry);
                if (decoded.Proposals.Count != 1) continue;
                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, decoded.Proposals[0].SourceParts!);
                if (!canonical.IsCanonical) continue;
                binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (binding.IsBound) return entry.Clone();
            }
        }
        throw new InvalidOperationException("No bindable V2 SRC-089 sourceParts entry found in committed raw run.");
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
