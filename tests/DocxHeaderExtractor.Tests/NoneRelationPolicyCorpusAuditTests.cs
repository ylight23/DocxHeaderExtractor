using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// What the approved corpus actually says about headings that hold no position in the section tree.
/// <para>
/// The prompt admits five kinds of them - a document's title and subtitle, running headers and
/// footers, table and figure labels, form and signature labels - and instructs that they are real
/// headings to be reported. On DOC-0252 that category came back with one right answer and four
/// wrong ones. Before narrowing an instruction that every lane and every document shares, the
/// question is what the rest of the corpus evidences.
/// </para>
/// <para>
/// The answer is mostly that it cannot say, and that is the finding: seventeen of the twenty-one
/// approved authorities record a heading total and no headings. This audit reports what the four
/// that do record them show, and states plainly how much of the corpus that is.
/// </para>
/// </summary>
public sealed class NoneRelationPolicyCorpusAuditTests
{
    private const string AuditRoot = "eval/a99-closed-loop/none-relation-policy-audit-v1";
    private const string Doc0252GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    [Fact]
    public void Audit_what_the_corpus_evidences_about_tree_less_headings()
    {
        Assert.Equal(Doc0252GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);

        var authorities = CanonicalGoldRegistry.Entries
            .Select(entry =>
            {
                // DOC-0252 is pinned to the R1 vintage this audit actually ran against; every
                // other authority resolves live because no other authority has been revised.
                using var gold = entry.AuthorityId == "DOC-0252"
                    ? CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256)
                    : CanonicalGoldRegistry.Resolve(entry.AuthorityId);
                var semantic = gold.RootElement.GetProperty("semantic");
                var claims = semantic.GetProperty("claims").EnumerateArray().ToArray();
                return new
                {
                    entry.AuthorityId,
                    // Read from the resolved (possibly R1-pinned) Gold itself, not the live
                    // registry entry, so a pinned historical vintage is reflected consistently.
                    approvedHeadings = semantic.GetProperty("semanticHeadingTotal").GetInt32(),
                    materializedClaims = claims.Length,
                    carriesClaimLevelData = claims.Length > 0,
                    roles = claims
                        .Select(claim => claim.TryGetProperty("semanticRole", out var role) && role.ValueKind == JsonValueKind.String
                            ? role.GetString()!
                            : "(none)")
                        .GroupBy(role => role, StringComparer.Ordinal)
                        .OrderByDescending(group => group.Count())
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                    texts = claims.Select(claim =>
                        claim.TryGetProperty("verbatimText", out var verbatim) && verbatim.ValueKind == JsonValueKind.String
                            ? verbatim.GetString()!
                            : claim.TryGetProperty("projectedText", out var projected) && projected.ValueKind == JsonValueKind.String
                                ? projected.GetString()!
                                : string.Empty).ToArray(),
                };
            })
            .ToArray();

        var withClaims = authorities.Where(item => item.carriesClaimLevelData).ToArray();
        var totalApproved = authorities.Sum(item => item.approvedHeadings);
        var totalMaterialized = authorities.Sum(item => item.materializedClaims);

        // The corpus can only answer for what it materializes, and it materializes very little.
        Assert.Equal(4, withClaims.Length);

        // ---- the only tree-less claim this audit's recommendation is scoped to -------------------
        var doc0252 = withClaims.Single(item => item.AuthorityId == "DOC-0252");
        var doc0001 = withClaims.Single(item => item.AuthorityId == "DOC-0001");
        var doc0205 = withClaims.Single(item => item.AuthorityId == "DOC-0205");
        var doc0258 = withClaims.Single(item => item.AuthorityId == "DOC-0258");
        var doc0258OrganisationHeadings = doc0258.texts.Count(text =>
            text is "African Development Bank (AfDB)" or "Asian Development Bank (ADB)"
                or "Interstate Statistical Committee of the Commonwealth of Independent States (CIS-STAT)"
                or "Organisation for Economic Co-operation and Development (OECD)"
                or "Statistical Office of the European Communities (Eurostat)"
                or "United Nations Economic Commission for Latin America and the Caribbean (UN-ECLAC)"
                or "United Nations Economic and Social Commission for Western Asia (UN-ESCWA)"
                or "International Monetary Fund (IMF)" or "World Bank");
        Assert.Equal(9, doc0258OrganisationHeadings);
        var documentTitleClaims = doc0252.roles.GetValueOrDefault("DocumentTitle");
        var doc0205DocumentTitleClaims = doc0205.roles.GetValueOrDefault("DocumentTitle");

        // What the model did with the category, on both lanes, from replies already captured.
        var noneUsage = NoneUsageFromCapturedReplies();

        FreezeArtifact.AssertJson(AuditRoot, "none-relation-policy-corpus-audit.v1.json", new
        {
            artifactKind = "a99_none_relation_policy_corpus_audit",
            schemaVersion = "a99-none-relation-policy-corpus-audit-v1",
            providerCalls = 0,
            modelCalls = 0,
            promptChanged = false,
            question = "Before narrowing a shared instruction, what does the approved corpus evidence about "
                + "headings that hold no position in the section tree?",

            // ---- the feasibility finding, stated first because it bounds every answer below -------
            corpusCoverage = new
            {
                authorities = authorities.Length,
                authoritiesCarryingClaimLevelData = withClaims.Length,
                approvedHeadingsInCorpus = totalApproved,
                materializedClaimsInCorpus = totalMaterialized,
                coverageOfApprovedHeadings = Math.Round((double)totalMaterialized / totalApproved, 4),
                finding = "Seventeen of twenty-one approved authorities record a heading total and no headings. "
                    + "Their own freeze artifacts say so: 'Semantic total is authoritative; no occurrence list "
                    + "or span was synthesized from the total.' A corpus-wide audit of which claims need a "
                    + "tree-less relation is therefore not answerable from Gold - "
                    + $"{totalMaterialized} of {totalApproved} approved headings, about "
                    + $"{Math.Round((double)totalMaterialized / totalApproved * 100, 1)}%, exist as claims at all.",
                consequence = "Everything below is evidence from four documents (DOC-0205 and DOC-0258 "
                    + "materialized after the other two) in three genres and two languages. It is enough to say "
                    + "what the category is used for where it is used, and not enough to say that no genre needs "
                    + "it wider.",
                byAuthority = authorities.Select(item => new
                {
                    item.AuthorityId,
                    item.approvedHeadings,
                    item.materializedClaims,
                    item.carriesClaimLevelData,
                }).ToArray(),
            },

            // ---- Q1 / Q3: which claims genuinely need a tree-less relation ------------------------
            treeLessClaimsInGold = new
            {
                total = documentTitleClaims,
                identities = new[] { "DOC-0252 L0000:S0 'MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM' (DocumentTitle)" },
                shareOfMaterializedClaims = Math.Round((double)documentTitleClaims / totalMaterialized, 4),
                byPromptCategory = new
                {
                    documentTitle = new { admittedByPrompt = true, evidencedInGold = documentTitleClaims, note = "one instance, DOC-0252" },
                    documentSubtitle = new { admittedByPrompt = true, evidencedInGold = 0, note = "no Gold claim in the corpus is a subtitle" },
                    runningHeaderOrFooter = new { admittedByPrompt = true, evidencedInGold = 0, note = "none" },
                    tableOrFigureLabel = new { admittedByPrompt = true, evidencedInGold = 0, note = "none" },
                    formOrSignatureLabel = new { admittedByPrompt = true, evidencedInGold = 0, note = "none" },
                },
                reading = "The prompt admits five categories. The corpus evidences one of them, once. That is "
                    + "not proof the other four are wrong - no corpus document contains an approved claim of "
                    + "those kinds either way - but it does mean four fifths of the category is asserted by "
                    + "the instruction rather than by anything approved.",
            },

            // ---- Q2: is the category a policy or a catch-all --------------------------------------
            howTheModelUsesIt = noneUsage,

            // ---- Q4: genre variation, as far as two genres allow ----------------------------------
            genreEvidence = new
            {
                unevidencedGenres = "17 authorities - textbooks, reports and other minutes among them - record "
                    + "totals only. Whether any of them contains an approved masthead, running header or table "
                    + "label cannot be read from Gold.",
                evidenced = new object[]
                {
                new
                {
                    authority = "DOC-0001",
                    genre = "administrative regulation, Vietnamese, DOCX lane",
                    approvedHeadings = doc0001.approvedHeadings,
                    claims = doc0001.texts,
                    treeLessClaims = 0,
                    modelTreeLessClaims = 0,
                    reading = "Every approved heading is a numbered structural unit and every claim the model "
                        + "returned was tree-positioned, in all three repeats. The category is never exercised "
                        + "here, so narrowing it cannot cost this document anything.",
                },
                new
                {
                    authority = "DOC-0205",
                    genre = "legal decree, Vietnamese, DOCX lane",
                    approvedHeadings = doc0205.approvedHeadings,
                    roles = doc0205.roles,
                    treeLessClaims = doc0205DocumentTitleClaims,
                    reading = "Materialized into canonical Gold after DOC-0001 and DOC-0252 (71 prior-approved "
                        + "chapter/article headings plus one human-approved addition, the document's own title). "
                        + "The corpus's Gold claims were never model-classified for this document, so unlike "
                        + "DOC-0001 and DOC-0252 there is no captured model reply to compare against - this row "
                        + "reports only what Gold itself contains, not how the tree-less category performed "
                        + "here. Its one DocumentTitle-role claim is the document's own opening title (position "
                        + "0), the same kind of claim the ontology review excludes by design as not evidencing "
                        + "dual-function ambiguity; it is reported here for completeness, not folded into the "
                        + "DOC-0252-scoped recommendation below.",
                },
                new
                {
                    authority = "DOC-0252",
                    genre = "meeting minutes with an agenda annex, English, PDF lane",
                    approvedHeadings = doc0252.approvedHeadings,
                    roles = doc0252.roles,
                    treeLessClaims = documentTitleClaims,
                    reading = "The only document in the corpus with a captured model reply that exercises the "
                        + "category, and the only one where it goes wrong. Its annex restates the event at the "
                        + "top of a page, which is exactly the shape the instruction's 'title and subtitle' "
                        + "clause invites.",
                },
                new
                {
                    authority = "DOC-0258",
                    genre = "meeting minutes with an agenda annex and a participant annex, English, DOCX lane",
                    approvedHeadings = doc0258.approvedHeadings,
                    roles = doc0258.roles,
                    organisationNamedHeadings = doc0258OrganisationHeadings,
                    reading = "Materialized after the other three (24 historical headings plus 13 kept by the user, "
                        + "37, on a source regenerated from its PDF); roles were not assigned, so tree-less claims "
                        + "cannot be counted from Gold. Two facts bear on the proposal below. First, nine approved "
                        + "headings are organisation names - each opens its own attendee list in Annex 2, so they "
                        + "are structural units, but their text is exactly what a clause worded 'text that "
                        + "identifies an organisation is not promoted' names. Second, three exploratory "
                        + "qwen3.7-flash runs on this document (eval/a99-closed-loop/doc0258-real-harness-"
                        + "exploration-v1, on the superseded source) reported the masthead date and meeting-mode "
                        + "lines as headings in all three - the same shape DOC-0252 shows. Their relation hints "
                        + "were not captured, so they are not counted as tree-less claims here.",
                },
                },
            },

            // ---- Q5: can the category be defined by function --------------------------------------
            proposedAbstraction = new
            {
                shape = "three kinds, defined by what the text does to the document's structure rather than by "
                    + "what kind of document it appears in",
                structuralHeading = "names or introduces a structural unit; content belongs under it",
                acceptedTreeLessLabel = "a document-level label the policy explicitly admits, which names the "
                    + "document as a whole rather than a part of it, and under which nothing is filed",
                contentOrMetadataOrItem = "may read as a title, but establishes no structural unit: it "
                    + "identifies an event or organisation, gives a date, venue, address or mode, repeats "
                    + "running furniture, or is one entry inside a structure that already exists",
                proposedWordingDirection = "Do not use the tree-less relation as a catch-all for prominent "
                    + "text. A claim in that relation must still name a document-level label the policy "
                    + "admits. Text that identifies an event, organisation, date, venue, address or meeting "
                    + "mode, or that is an entry inside an existing structure, is not promoted because it "
                    + "reads like a title.",
                deliberatelyNotProposed = new[]
                {
                    "any rule mentioning times, dates or address formats",
                    "any rule keyed to a document genre",
                    "any wording drawn from DOC-0252",
                },
                evidenceStrength = $"Supported where the corpus can speak, and the corpus speaks for "
                    + $"{Math.Round((double)totalMaterialized / totalApproved * 100, 1)}% of its own approved "
                    + "headings. The narrowing is safe for the one document that never uses the category and "
                    + "corrects four of five errors in the one that does; beyond those two (DOC-0205 has no "
                    + "captured model reply to measure against, and DOC-0258's replies carry no relation hints) "
                    + "it is a judgement, not a measurement. The organisation wording in particular is not "
                    + "safe as written: DOC-0258 has nine approved structural headings whose text is an "
                    + "organisation name, and the clause would have to be reworded to exempt a label that "
                    + "opens its own unit before it is carried beyond DOC-0252.",
            },

            recommendation = new
            {
                proceedToExperiment = "EXP_MASTHEAD_METADATA",
                scope = "DOC-0252 PACK_005 and PACK_006, three repeats, 6 semantic calls, proposed cap 9",
                why = "The cross-corpus question with a captured model reply to check is answerable only for "
                      + "two documents, and both of them point the same way. DOC-0205's and DOC-0258's Gold were "
                      + "materialized without a relation-hint capture, so they cannot inform this comparison "
                      + "either way. "
                      + $"Waiting for corpus-wide claim data would mean materializing {totalApproved - totalMaterialized} "
                      + "more approved headings that no one has been asked to materialize - a far larger piece "
                      + "of work than the change it would inform.",
                doFirst = "State the narrowing by function, and measure it on the one document that exercises "
                    + "the category, before it is carried to lanes whose documents have never been claim-level "
                    + "evaluated.",
                residualRisk = "A genre that legitimately wants a masthead kept would regress silently, because "
                    + "no approved claim anywhere in the corpus would catch it. Recorded here rather than "
                    + "resolved.",
                experimentNames = new[] { "EXP_MASTHEAD_METADATA", "EXP_BODY_PROPOSITION", "EXP_SCHEDULE_ITEM" },
            },
        });

        // The two claims this audit turns on, asserted so the artifact cannot drift from them.
        Assert.Equal(1, documentTitleClaims);
        Assert.Equal(0, doc0001.roles.Count(role => role.Key != "(none)"));
    }

    /// <summary>
    /// How the tree-less relation was actually used, counted from replies already captured on both
    /// lanes - the DOCX baseline and the structured PDF rerun - rather than assumed.
    /// </summary>
    private static object NoneUsageFromCapturedReplies()
    {
        var docx = CountRelations(
            "eval/a99-closed-loop/occurrence-baseline-structured-v1/raw-responses.v1.json", "DOC-0001");

        var directory = TestRepository.Path(
            "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252/r1");
        using var capture = JsonDocument.Parse(
            File.ReadAllText(Directory.GetFiles(directory, "*transport-capture.v1.json").Single()));
        var pdfNone = 0;
        var pdfTotal = 0;
        foreach (var call in capture.RootElement.GetProperty("calls").EnumerateArray())
        {
            var raw = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!));
            using var response = JsonDocument.Parse(raw);
            foreach (var heading in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                pdfTotal++;
                if (Hints(heading).Contains("parent-node:NONE")) pdfNone++;
            }
        }

        return new
        {
            docxLane = new
            {
                document = "DOC-0001",
                claims = docx.Total,
                treeLessClaims = docx.None,
                note = "Never used. Every claim was placed under ROOT or under another heading, in all "
                    + "three repeats, and precision was 7 of 7.",
            },
            pdfLane = new
            {
                document = "DOC-0252",
                claims = pdfTotal,
                treeLessClaims = pdfNone,
                treeLessThatAreGold = 1,
                treeLessThatAreFalsePositives = 4,
                precisionOfTheCategory = Math.Round(1.0 / pdfNone, 4),
                note = "One right answer in six. The category is carrying the annex masthead, the meeting "
                    + "mode, the date-venue-address block and a prose fragment, all of which the instruction "
                    + "can be read to invite.",
            },
            reading = "Used as a policy on one lane and as a catch-all on the other, from the same "
                + "instruction. What separates them is not the lane but whether the document contains "
                + "prominent text that is not structural - and this instruction gives the model no way to "
                + "say so except by reporting it.",
        };
    }

    private static (int Total, int None) CountRelations(string artifact, string documentId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(artifact)));
        var total = 0;
        var none = 0;
        foreach (var call in document.RootElement.EnumerateArray())
        {
            if (call.GetProperty("DocumentId").GetString() != documentId) continue;
            using var response = JsonDocument.Parse(call.GetProperty("RawResponse").GetString()!);
            foreach (var heading in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                total++;
                if (Hints(heading).Contains("parent-node:NONE")) none++;
            }
        }
        return (total, none);
    }

    private static IReadOnlyList<string> Hints(JsonElement heading) =>
        heading.TryGetProperty("relationHints", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];
}
