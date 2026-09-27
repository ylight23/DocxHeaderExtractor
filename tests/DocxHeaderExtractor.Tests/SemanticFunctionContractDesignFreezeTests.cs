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
    private static readonly HashSet<string> MembershipArmFields = new(
        ["sourceParts", "semanticFunction"], StringComparer.Ordinal);

    private sealed record FunctionDefinition(string Function, bool Member, string Definition, string Boundary);

    private static readonly FunctionDefinition[] Definitions =
    [
        new("DOCUMENT_IDENTITY", true,
            "Identifies the document, report, or artifact itself as its semantic title identity.",
            "Unlike METADATA, it is the title identity rather than a description of the artifact."),
        new("REGION_STRUCTURE", true,
            "Names or opens a semantic region whose subsequent content belongs beneath it.",
            "Unlike NAVIGATION, OBJECT_CAPTION, and TABLE_STRUCTURE, it opens a region rather than pointing elsewhere or describing an internal object/table part."),
        new("NAVIGATION", false,
            "Points to, lists, or leads to content elsewhere in the document.",
            "Unlike REGION_STRUCTURE, it does not open a region whose following content belongs beneath it."),
        new("PAGE_FURNITURE", false,
            "Serves repeated or page-positioned presentation, such as a running header, footer, or page number.",
            "Unlike REGION_STRUCTURE, its function is page presentation, not opening a content region."),
        new("OBJECT_CAPTION", false,
            "Names or describes an embedded visual, figure, listing, or other object whose scope is that object itself.",
            "Unlike REGION_STRUCTURE, it does not open a broader following content region."),
        new("TABLE_STRUCTURE", false,
            "Is a row, column, label, or header structure internal to a table.",
            "Unlike REGION_STRUCTURE, it is internal to the table rather than a heading outside or around it that opens a broader region."),
        new("FOOTNOTE_OR_SOURCE", false,
            "Is a footnote, citation, attribution, or source note that supports or qualifies other content.",
            "Unlike REGION_STRUCTURE, it does not establish a region for subsequent body content."),
        new("BODY_INFORMATION", false,
            "Is ordinary body information, such as prose, a statement, or a run-in fact, without a region-opening function.",
            "Unlike REGION_STRUCTURE, it conveys content but does not name or open the region containing it."),
        new("METADATA", false,
            "Describes the document, its provenance, status, date, audience, or other attributes without constituting its title identity.",
            "Unlike DOCUMENT_IDENTITY, it describes the artifact but is not the artifact's semantic title identity."),
    ];

    private sealed record Decision(
        string SemanticFunction,
        bool IsMember,
        string SourcePartsJson);

    private sealed record V2BindingInvariant(string SourcePartsSha256, string BoundIdentity);

    private static readonly Lazy<V2BindingInvariant> RawV2Binding = new(ReadOneBoundV2Coordinate);

    [Fact]
    public void The_design_schema_has_one_closed_membership_authority()
    {
        var schema = JsonSerializer.Serialize(MembershipArmSchema());

        Assert.DoesNotContain("isHeading", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("semanticRole", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("occurrenceRole", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("scope", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("titleRelation", schema, StringComparison.Ordinal);
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
            using var entry = JsonDocument.Parse($$"""{"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"{{function}}"}""");
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
    public void First_membership_arm_rejects_descriptive_axes_to_keep_the_treatment_attributable()
    {
        foreach (var field in new[] { "occurrenceRole", "scope", "titleRelation" })
        {
            using var entry = JsonDocument.Parse($$"""
                {"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"REGION_STRUCTURE","{{field}}":"anything"}
                """);
            Assert.False(TryDecode(entry.RootElement, out _, out var issue));
            Assert.Equal($"FIELD_NOT_IN_CONTRACT:{field}", issue);
        }
    }

    [Fact]
    public void All_nine_functions_have_normative_definitions_and_fixed_boundary_cases()
    {
        Assert.Equal(Functions, Definitions.Select(definition => definition.Function));
        Assert.All(Definitions, definition =>
        {
            Assert.NotEmpty(definition.Definition);
            Assert.NotEmpty(definition.Boundary);
            using var entry = JsonDocument.Parse($$"""{"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"{{definition.Function}}"}""");
            Assert.True(TryDecode(entry.RootElement, out var decoded, out var issue), issue);
            Assert.Equal(definition.Member, decoded!.IsMember);
        });

        Assert.Contains("title identity", Definition("DOCUMENT_IDENTITY").Definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("describes the artifact", Definition("METADATA").Boundary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("embedded visual", Definition("OBJECT_CAPTION").Definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal to a table", Definition("TABLE_STRUCTURE").Definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opens a semantic region", Definition("REGION_STRUCTURE").Definition, StringComparison.OrdinalIgnoreCase);
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
             "semanticFunction":"NAVIGATION"}
            """);
        using var body = JsonDocument.Parse("""
            {"sourceParts":[{"sourceAlias":"L0517:S0","verbatimText":"3.2 Server Push"}],
             "semanticFunction":"REGION_STRUCTURE"}
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

        // V2 remains immutable even once a later, explicit-only V4 transport exists.
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
        Assert.Contains(SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY,
            Enum.GetValues<SemanticRequestVersion>());
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
            status = "design_freeze_with_explicit_only_v4_transport_preflighted_not_provider_authorized",
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
                normativeDefinitions = Definitions.Select(definition => new
                {
                    semanticFunction = definition.Function,
                    member = definition.Member,
                    definition = definition.Definition,
                    boundary = definition.Boundary,
                }).ToArray(),
                occurrenceNotString = "identical or overlapping text may have different functions at different source occurrences and contexts",
            },
            membershipFirstArm = new
            {
                modelVisibleOutput = new[] { "sourceParts", "semanticFunction" },
                schema = MembershipArmSchema(),
                descriptiveAxesExcluded = new[] { "occurrenceRole", "scope", "titleRelation" },
                purpose = "isolate whether the model distinguishes REGION_STRUCTURE from NAVIGATION before hierarchy/descriptive axes add task complexity",
                executionAfterAuthorizationOnly = new
                {
                    requestVersion = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
                    model = "qwen/qwen3.7-flash",
                    sourceFacts = "V3",
                    packing = 120,
                    sourceParts = "existing binder contract",
                    cohort = new[] { "SRC-089", "SRC-095" },
                    gold = "unopened until raw predictions are persisted",
                },
            },
            targetArchitectureOnly = new
            {
                possibleDescriptiveAxes = new[] { "occurrenceRole", "scope", "titleRelation" },
                membershipAuthority = false,
                cannotOverrideSemanticFunction = true,
                deferredUntil = "a later hierarchy-focused arm after membership is measured",
            },
            deterministicProofs = new[]
            {
                "isHeading is absent and rejected as a field not in the contract",
                "generic heading is not an enum value and fails closed",
                "unknown semanticFunction fails closed",
                "membership is derived only from semanticFunction",
                "the first model-visible membership arm permits only sourceParts and semanticFunction; descriptive axes are rejected",
                "all nine functions have normative definitions, including DOCUMENT_IDENTITY/METADATA, REGION_STRUCTURE/OBJECT_CAPTION, and REGION_STRUCTURE/TABLE_STRUCTURE boundaries",
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
            nextGate = "Do not send the explicit-only V4 transport until its frozen preflight is reviewed and provider authorization is explicit; the arm must use this closed vocabulary rather than project legacy semanticRole.",
        });
    }

    private static object MembershipArmSchema() => new
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
            if (!MembershipArmFields.Contains(property.Name))
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
        decision = new Decision(value, value is "DOCUMENT_IDENTITY" or "REGION_STRUCTURE", sourceParts.GetRawText());
        return true;
    }

    private static FunctionDefinition Definition(string semanticFunction) =>
        Definitions.Single(definition => definition.Function == semanticFunction);

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
