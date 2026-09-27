using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99_SEMANTIC_AUTHORITY_CONSISTENCY_AUDIT_V1 - phase B. The reachability audit of 2026-09-27 recorded that the contract
/// still carries logically independent <c>isHeading</c> and <c>semanticRole</c> fields, and left it as debt. This measures
/// that debt: which decision fields each model-facing contract exposes, what the contract constrains about their
/// combination, which contradictory states it accepts as valid, and which of those actually occurred in the two committed
/// LLM runs.
/// <para>
/// Every contradiction below is constructed here and put through the production validator and decoder, so "the contract
/// accepts it" is executed, not asserted from reading. Nothing is repaired: phase C proposes, and no redesign is
/// implemented. 0 provider calls.
/// </para>
/// </summary>
public sealed class SemanticAuthorityConsistencyAuditTests
{
    private const string Dir = "eval/a99-closed-loop/semantic-authority-consistency-v1";

    /// <summary>A contradictory-but-schema-valid reply, and what makes it contradictory.</summary>
    private sealed record Case(string Id, string Reason, string Json);

    private static readonly Case[] Cases =
    [
        new("EXCLUDED_ROLE_IS_HEADING",
            "the role names page furniture while isHeading says it is a heading - the state the pilot and the V3 arm both produced",
            """{"headings":[{"isHeading":true,"semanticRole":"running-header","sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
        new("FOOTNOTE_ROLE_IS_HEADING",
            "the role names a footnote while isHeading says heading (SRC-089, both arms)",
            """{"headings":[{"isHeading":true,"semanticRole":"footnote","sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
        new("HEADING_ROLE_IS_NOT_HEADING",
            "the mirror image: the role names a section heading while isHeading says it is not one",
            """{"headings":[{"isHeading":false,"semanticRole":"section-heading","sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
        new("NOT_HEADING_WITH_PARENT_RELATION",
            "isHeading is false yet the reply places the occurrence in the heading tree",
            """{"headings":[{"isHeading":false,"semanticRole":"index-entry","relationHints":["parent-node:L0002:S0"],"sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
        new("EMPTY_ROLE_IS_HEADING",
            "a heading with no role at all: semanticRole is optional, so the meaning axis can simply be absent",
            """{"headings":[{"isHeading":true,"sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
        new("UNDECLARED_FIELD",
            "a field the schema does not declare (additionalProperties is false), which the arm's model actually sent once as semanticRoleNote",
            """{"headings":[{"isHeading":true,"semanticRole":"heading","semanticRoleNote":"anything","sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
        new("INVENTED_ROLE_VOCABULARY",
            "a role no vocabulary defines: the field is an unconstrained string, so a new name is as valid as any other",
            """{"headings":[{"isHeading":true,"semanticRole":"qqq-not-a-role-42","sourceParts":[{"sourceAlias":"L0001:S0","selectionMode":"WHOLE_ALIAS"}]}]}"""),
    ];

    /// <summary>The same contradictions in the alias-span shape, which also carries structuralType and scope.</summary>
    private static readonly Case[] AliasCases =
    [
        new("ROLE_AGAINST_STRUCTURAL_TYPE",
            "semanticRole says running header, structuralType says section, scope says document, isHeading says true: four axes, no stated relation between them",
            """{"headings":[{"sourceAlias":"S0001","isHeading":true,"semanticRole":"running-header","structuralType":"section","scope":"document"}]}"""),
        new("NOT_HEADING_WITH_STRUCTURAL_TYPE",
            "isHeading is false while structuralType still declares a structural unit",
            """{"headings":[{"sourceAlias":"S0001","isHeading":false,"structuralType":"chapter","scope":"document"}]}"""),
    ];

    /// <summary>What the production validator and decoder did with one constructed reply.</summary>
    private sealed record CaseResult(string CaseId, string Reason, bool SchemaValid, string[] ValidatorIssues,
        int DecodedProposals, string[] DecodeFailures, bool AcceptedAsValid);

    private static IReadOnlyList<CaseResult> Measure(SemanticCoordinateContract contract, IReadOnlyList<Case> cases) =>
        cases.Select(item =>
        {
            using var document = JsonDocument.Parse(item.Json);
            var issues = contract.Validate(document.RootElement);
            var entry = document.RootElement.GetProperty("headings")[0];
            var decoded = contract.Decode(entry);
            return new CaseResult(item.Id, item.Reason, issues.Count == 0, issues.Select(i => i.ToString()).ToArray(),
                decoded.Proposals.Count, decoded.Failures.Select(f => f.ToString()).ToArray(), issues.Count == 0 && decoded.Proposals.Count > 0);
        }).ToArray();

    /// <summary>What the two committed runs actually answered, per decision field.</summary>
    private static object Observed(string root, string commit)
    {
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/run.v1.json")));
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        int entries = 0, headingTrue = 0, headingFalse = 0, headingAbsent = 0, roleAbsent = 0;
        var undeclared = new Dictionary<string, int>(StringComparer.Ordinal);
        var declaredFields = new[] { "isHeading", "semanticRole", "relationHints", "sourceParts" };
        var excludedRoles = new[] { "page_furniture", "page-furniture", "running-header", "running-footer", "page-header", "page-footer",
            "footnote", "source-note", "index-entry", "toc-entry", "table-header", "table-caption", "figure-caption", "figure-label",
            "caption", "signature-label", "signature-value" };
        int excludedRoleEntries = 0, excludedRoleClaimedAsHeading = 0;
        foreach (var call in run.RootElement.GetProperty("ledger").EnumerateArray())
        {
            var text = call.GetProperty("Response").GetString();
            if (text is null) continue;
            using var response = JsonDocument.Parse(text);
            if (!response.RootElement.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in headings.EnumerateArray())
            {
                entries++;
                foreach (var property in entry.EnumerateObject())
                {
                    fields[property.Name] = fields.GetValueOrDefault(property.Name) + 1;
                    if (!declaredFields.Contains(property.Name, StringComparer.Ordinal))
                        undeclared[property.Name] = undeclared.GetValueOrDefault(property.Name) + 1;
                }
                if (!entry.TryGetProperty("isHeading", out var flag)) headingAbsent++;
                else if (flag.ValueKind == JsonValueKind.False) headingFalse++;
                else headingTrue++;
                if (entry.TryGetProperty("semanticRole", out var role) && role.ValueKind == JsonValueKind.String)
                {
                    var name = role.GetString()!;
                    roles[name] = roles.GetValueOrDefault(name) + 1;
                    if (excludedRoles.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        excludedRoleEntries++;
                        if (!entry.TryGetProperty("isHeading", out var f) || f.ValueKind != JsonValueKind.False) excludedRoleClaimedAsHeading++;
                    }
                }
                else roleAbsent++;
            }
        }
        return new
        {
            run = new { path = $"{root}/run.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{root}/run.v1.json")), commit },
            entries,
            isHeading = new { @true = headingTrue, @false = headingFalse, absent = headingAbsent },
            fieldsUsed = fields.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Value),
            semanticRoleAbsent = roleAbsent,
            distinctSemanticRoles = roles.Count,
            undeclaredFieldsSent = undeclared,
            selfContradictingEntries = new
            {
                excludedRoleEntries,
                excludedRoleClaimedAsHeading,
                reading = excludedRoleEntries == excludedRoleClaimedAsHeading
                    ? "every occurrence the model itself named page furniture, a note, an index entry, a caption, a table header or a signature line was also claimed as a heading"
                    : "some were declined",
            },
            semanticRoleVocabularyObserved = roles.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).ToDictionary(r => r.Key, r => r.Value),
        };
    }

    [Fact]
    public void Freeze_the_semantic_authority_consistency_audit()
    {
        var structured = SemanticCoordinateContract.PdfStructuredSourceParts;
        var structuredV2 = SemanticCoordinateContract.PdfStructuredSourcePartsV2;
        var aliasSpan = SemanticCoordinateContract.DocxAliasSpan;

        var results = new[]
        {
            new { contract = structured.CoordinateSystem + " / " + structured.ProtocolVersion, cases = Measure(structured, Cases) },
            new { contract = structuredV2.CoordinateSystem + " / " + structuredV2.ProtocolVersion, cases = Measure(structuredV2, Cases) },
            new { contract = aliasSpan.CoordinateSystem + " / " + aliasSpan.ProtocolVersion, cases = Measure(aliasSpan, AliasCases) },
        };
        var accepted = results.SelectMany(r => r.cases).Count(c => c.AcceptedAsValid);
        var total = results.Sum(r => r.cases.Count);

        // What the global detector is for, read from its own kinds: disagreement between two proposals about one
        // occurrence. A single proposal contradicting itself across its own fields is not one of its kinds.
        var detectorKinds = new[] { "PARENT_RELATION_CONTRADICTION", "STRUCTURAL_TYPE_CONTRADICTION", "SEMANTIC_ROLE_CONTRADICTION", "SCOPE_CONTRADICTION", "RELATION_HINT_CONTRADICTION" };
        var singleProposal = new[]
        {
            new CanonicalSemanticProposal("S0001", IsHeading: true, VerbatimText: null, SemanticRole: "running-header", StructuralType: "section", Scope: "document"),
        };
        var aliases = new[] { new SemanticSourceAlias("S0001", "p1", 1, "RFC 9114 HTTP/3 June 2022", new StructuralSpan(0, 25)) };
        var detected = CanonicalSemanticGlobalConflictDetector.Detect(singleProposal, aliases);

        FreezeArtifact.AssertJson(Dir, "semantic-authority-consistency.v1.json", new
        {
            artifactKind = "a99_semantic_authority_consistency_audit",
            study = "A99_SEMANTIC_AUTHORITY_CONSISTENCY_AUDIT_V1",
            phase = "B - what the contract lets a semantic decision be",
            follows = new { audit = "docs/architecture/current-semantic-reachability-audit-2026-09-27.md", commit = "16f5273", item = "the isHeading/semanticRole debt recorded in its authority graph" },
            modelProviderVlmCalls = 0,
            decisionFields = new object[]
            {
                new { field = "isHeading", type = "boolean", required = true, constrainedBy = "nothing but its type", contracts = new[] { "a99-semantic-source-parts-v1", "a99-semantic-source-parts-v2", "a99-canonical-semantic-vnext-v1" } },
                new { field = "semanticRole", type = "string", required = false, constrainedBy = "nothing: no enum, no vocabulary, no relation to isHeading", contracts = new[] { "a99-semantic-source-parts-v1", "a99-semantic-source-parts-v2", "a99-canonical-semantic-vnext-v1" } },
                new { field = "structuralType", type = "string", required = false, constrainedBy = "nothing", contracts = new[] { "a99-canonical-semantic-vnext-v1" } },
                new { field = "scope", type = "string", required = false, constrainedBy = "nothing", contracts = new[] { "a99-canonical-semantic-vnext-v1" } },
                new { field = "relationHints", type = "string[]", required = false, constrainedBy = "parsed for parent-node and same-node keys; not checked against isHeading", contracts = new[] { "a99-semantic-source-parts-v1", "a99-semantic-source-parts-v2", "a99-canonical-semantic-vnext-v1" } },
            },
            contradictionsAccepted = new { accepted, total, everyConstructedContradictionAccepted = accepted == total },
            byContract = results,
            existingConflictDetection = new
            {
                component = "CanonicalSemanticGlobalConflictDetector",
                kinds = detectorKinds,
                scope = "two or more proposals that describe the same physical occurrence and disagree with each other",
                selfContradictionOfOneProposal = new
                {
                    probe = "one proposal, isHeading true with semanticRole running-header, structuralType section and scope document",
                    conflictsDetected = detected.Count,
                    reading = "none: an occurrence claimed once is never compared with itself, so a reply that contradicts itself across its own fields passes every layer",
                },
            },
            observedInCommittedRuns = new
            {
                baselineV2 = Observed(LlmSemanticPilotV1Tests.Root, "4ab732d"),
                armV3 = Observed(LlmSemanticExclusionArmV1Tests.Root, "ce560c0"),
                note = "semanticRole is a free string, and the two runs show it behaving like one: the arm invented names the baseline never used (document-subtitle, running-footer, table-label) while claiming the same occurrences",
            },
            findings = new[]
            {
                "isHeading carried no information in either run: it was true on every entry of both (300 of 300 and 301 of 301). Membership was decided in practice by whether the model put an entry in the array at all, not by the field the contract requires for it",
                "in the V3 arm the model wrote semanticRole \"page_furniture\" - the clause's own excluded category - with isHeading true. The role it chose and the membership it claimed contradict each other in the same object, and nothing in the pipeline reads the pair",
                "membership and meaning are two independent truths in every current contract. isHeading is required and unconstrained; semanticRole is optional, free-form, and carries no stated relation to it",
                "every contradictory state constructed here is accepted as valid by the production validator and decoder, including a heading whose role is page furniture, a non-heading whose role is a section heading, a non-heading placed in the tree by a parent relation, and a heading with no role at all",
                "the existing global conflict detector is about disagreement between proposals for one occurrence. It has no kind for a single proposal contradicting itself, and detects nothing for the probe above",
                "so the pipeline has no layer that can refuse the reply the pilot measured: the binder validates coordinates, the identity resolver merges, the hierarchy resolver reads relations - none of them reads semanticRole at all",
                "because semanticRole is unconstrained, a consistency rule stated over role names could be satisfied by inventing a name outside the rule - which is what the V3 arm did (SRC-089's contradictions fell to 0 while its false claims rose from 10 to 28)",
            },
            whatThisDoesNotShow = new[]
            {
                "that a coherent representation would have scored better: no arm was run for it, and this audit changes nothing",
                "that semanticRole is unused downstream by intent - it is carried into artifacts and read by reviewers and by the scorer's axis comparison, so removing it is not the only option",
            },
        });

        Assert.Equal(total, accepted); // the debt is that all of them are valid
        Assert.Empty(detected); // and that nothing detects a self-contradicting proposal
    }
}
