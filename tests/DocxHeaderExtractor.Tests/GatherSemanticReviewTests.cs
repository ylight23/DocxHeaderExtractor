using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.Tests;

public sealed class GatherSemanticReviewTests
{
    sealed class Model(string reply) : IGatherModel
    {
        public int Calls;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) { Calls++; return Task.FromResult(reply); }
    }

    sealed class PartialBatchModel : IGatherModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(
            user.Contains("body[1]/p[2]", StringComparison.Ordinal) ? "{}" :
            "{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"supported\"}]}");
    }

    sealed class ProposalAndRepairModel(string proposal, string repair) : IGatherModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(
            system == GatherQuoteRepair.System ? repair : proposal);
    }

    sealed class JsonModeModel(string reply) : IGatherModel
    {
        public bool SawPartChecks;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            if (!system.Contains("json", StringComparison.OrdinalIgnoreCase) && !user.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("JSON_MODE_REQUIRES_JSON_MESSAGE");
            SawPartChecks = user.Contains("partChecks", StringComparison.Ordinal) && user.Contains("NOT_FOUND", StringComparison.Ordinal) && user.Contains("UNIQUE", StringComparison.Ordinal);
            return Task.FromResult(reply);
        }
    }

    sealed class RetryRepairModel(string proposal, string badRepair, string goodRepair) : IGatherModel
    {
        public int RepairCalls;
        public bool SawFeedback;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            if (system != GatherQuoteRepair.System) return Task.FromResult(proposal);
            RepairCalls++;
            if (RepairCalls == 1) return Task.FromResult(badRepair);
            SawFeedback = user.Contains("REPAIR_INVALID:QUOTE_NOT_FOUND", StringComparison.Ordinal) && user.Contains("previousAttempt", StringComparison.Ordinal);
            return Task.FromResult(goodRepair);
        }
    }

    sealed class NoRetryRepairModel(string proposal) : IGatherModel
    {
        public int RepairCalls;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            if (system != GatherQuoteRepair.System) return Task.FromResult(proposal);
            RepairCalls++;
            throw new GatherModelFailureException("BUDGET:EXHAUSTED", false);
        }
    }

    static (GatherSource Source, GatherRun Run) Fixture()
    {
        var path = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Đơn vị: Sở A</w:t></w:r></w:p><w:p><w:r><w:t>Ngân sách: 20 tỷ</w:t></w:r></w:p>");
        try
        {
            var source = GatherUnits.Read(path, "src");
            var proposals = source.Units.Select(u => new ProposedFact([new FactPart(u.Id, -1, -1, "text", u.Text)])).ToList();
            var checkedFacts = GatherCheck.Check(source.Units, proposals);
            return (source, new GatherRun(checkedFacts.Kept, [], [], [], [], [], 1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Verdicts_are_source_bound_and_do_not_mutate_the_original_selection()
    {
        var (source, run) = Fixture();
        var model = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"relevant\"},{\"id\":1,\"decision\":\"EXCLUDE\",\"reason\":\"outside goal\"}]}");
        var review = await GatherSemanticReview.ReviewAsync("Thông tin chung", run, [source], model);
        Assert.Equal(["KEEP", "EXCLUDE"], review.Verdicts.Select(v => v.Decision));
        Assert.Equal(2, run.Facts.Count);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Duplicate_missing_invalid_and_unknown_verdicts_never_become_keep()
    {
        var (source, run) = Fixture();
        var duplicate = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"yes\"},{\"id\":0,\"decision\":\"EXCLUDE\",\"reason\":\"no\"}]}");
        var review = await GatherSemanticReview.ReviewAsync("Mục", run, [source], duplicate);
        Assert.Equal(["DUPLICATE_VERDICT", "MISSING_VERDICT"], review.Verdicts.Select(v => v.Reason));
        Assert.All(review.Verdicts, v => Assert.Equal("UNSURE", v.Decision));
        var unknown = await GatherSemanticReview.ReviewAsync("Mục", run, [source], new Model("{\"verdicts\":[{\"id\":99,\"decision\":\"KEEP\",\"reason\":\"yes\"}]}"));
        Assert.Equal("REVIEW_UNKNOWN_ID", unknown.Error);
        Assert.All(unknown.Verdicts, v => Assert.Equal("UNSURE", v.Decision));
    }

    [Fact]
    public async Task Changed_source_is_rejected_before_calling_the_model()
    {
        var (source, run) = Fixture();
        var altered = source with { Units = source.Units.Select(u => u with { Text = "khác" }).ToList() };
        var model = new Model("{}");
        var review = await GatherSemanticReview.ReviewAsync("Mục", run, [altered], model);
        Assert.Equal("SOURCE_MISMATCH", review.Error);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Batched_review_keeps_original_indexes_and_reports_failed_batches_as_unsure()
    {
        var (source, run) = Fixture();
        var result = await GatherBatchedReview.ReviewAsync("Thông tin chung", run, [source], new PartialBatchModel(), batchSize: 1, concurrency: 2);
        Assert.Equal([0, 1], result.Review.Verdicts.Select(v => v.Index));
        Assert.Equal(["KEEP", "UNSURE"], result.Review.Verdicts.Select(v => v.Decision));
        var failure = Assert.Single(result.Failures);
        Assert.Equal(1, failure.Start);
        Assert.Equal(1, failure.Count);
        Assert.Equal("REVIEW_FORMAT_INVALID", failure.Error);
    }

    [Fact]
    public async Task Scope_pipeline_proposes_from_source_then_reviews_and_exposes_unproposed_units()
    {
        var (source, _) = Fixture();
        var first = source.Units[0];
        var reply = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] { new { unitId = first.Id, quote = first.Text, role = "text" } } } } });
        var proposer = new Model(reply);
        var reviewer = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"supported\"}]}");
        var result = await GatherScopePipeline.RunAsync("Thông tin chung", "Mục", "body[1]/p[1]", null, [source], proposer, reviewer);
        Assert.Equal([source.Units[0].Text, source.Units[1].Text], result.ReviewedSection.Run.Facts.Select(f => f.Render()));
        Assert.DoesNotContain("SCOPE_UNREVIEWED", result.ReviewedSection.Run.Facts[0].Flags);
        Assert.Contains(source.Units[1].Id, result.UnproposedUnitIds);
        Assert.Empty(result.UnresolvedUnitIds);
        Assert.Equal(1, proposer.Calls);
        Assert.Equal(2, reviewer.Calls);
    }

    [Fact]
    public async Task Duplicate_proposals_retain_source_provenance_and_are_not_unproposed()
    {
        var path = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Giống nhau</w:t></w:r></w:p><w:p><w:r><w:t>Giống nhau</w:t></w:r></w:p>");
        try
        {
            var source = GatherUnits.Read(path, "src");
            var reply = System.Text.Json.JsonSerializer.Serialize(new { facts = source.Units.Select(u => new { parts = new[] { new { unitId = u.Id, quote = u.Text, role = "text" } } }).ToArray() });
            var result = await GatherScopePipeline.RunAsync("Mục", "Mục", "", null, [source], new Model(reply),
                new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"yes\"}]}"));
            Assert.Single(result.Selection.Duplicates);
            Assert.Empty(result.UnproposedUnitIds);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unproposed_unit_with_unsure_coverage_stays_unresolved_and_blocked()
    {
        var (source, _) = Fixture();
        var first = source.Units[0];
        var reply = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] { new { unitId = first.Id, quote = first.Text, role = "text" } } } } });
        var unsure = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"UNSURE\",\"reason\":\"needs review\"}]}");
        var result = await GatherScopePipeline.RunAsync("Mục", "Mục", "", null, [source], new Model(reply), unsure);
        Assert.Contains(source.Units[1].Id, result.UnresolvedUnitIds);
        Assert.All(result.ReviewedSection.Run.Facts, fact => Assert.Contains("SCOPE_UNREVIEWED", fact.Flags));
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task Quote_repair_binds_only_an_exact_quote_from_the_original_unit()
    {
        var (source, _) = Fixture();
        var unit = source.Units[0];
        var refused = new[] { new FactRefusal(7, "QUOTE_NOT_FOUND", new ProposedFact([
            new FactPart(unit.Id, -1, -1, "label", "Thông tin chung"), new FactPart(unit.Id, -1, -1, "value", "Sở A")])) };
        var good = System.Text.Json.JsonSerializer.Serialize(new { repairs = new[] { new { index = 7, decision = "REPLACE", reason = "exact text",
            parts = new[] { new { unitId = unit.Id, quote = unit.Text, role = "text" } } } } });
        var model = new JsonModeModel(good);
        var result = await GatherQuoteRepair.RepairAsync("Mục", refused, [source], model);
        Assert.True(model.SawPartChecks);
        var repaired = Assert.Single(result.Decisions);
        Assert.Equal("REPAIRED", repaired.Status);
        Assert.Equal(unit.Text, repaired.Fact!.Render());
        Assert.Empty(result.Remaining);

        var wrongUnit = System.Text.Json.JsonSerializer.Serialize(new { repairs = new[] { new { index = 7, decision = "REPLACE", reason = "wrong source",
            parts = new[] { new { unitId = source.Units[1].Id, quote = source.Units[1].Text, role = "text" } } } } });
        var rejected = await GatherQuoteRepair.RepairAsync("Mục", refused, [source], new Model(wrongUnit));
        Assert.Equal("UNRESOLVED", Assert.Single(rejected.Decisions).Status);
        Assert.Single(rejected.Remaining);
    }

    [Fact]
    public async Task Quote_repair_does_not_accept_duplicate_or_missing_decisions()
    {
        var (source, _) = Fixture();
        var unit = source.Units[0];
        var refused = new[] { new FactRefusal(1, "QUOTE_NOT_FOUND", new ProposedFact([new FactPart(unit.Id, -1, -1, "text", "bad")])),
            new FactRefusal(2, "QUOTE_NOT_FOUND", new ProposedFact([new FactPart(unit.Id, -1, -1, "text", "bad")])) };
        var response = "{\"repairs\":[{\"index\":1,\"decision\":\"UNSURE\",\"reason\":\"no\",\"parts\":[]},{\"index\":1,\"decision\":\"UNSURE\",\"reason\":\"no\",\"parts\":[]}]}";
        var result = await GatherQuoteRepair.RepairAsync("Mục", refused, [source], new Model(response));
        Assert.Equal(2, result.Remaining.Count);
        Assert.All(result.Decisions, d => Assert.Equal("UNRESOLVED", d.Status));
    }

    [Fact]
    public async Task Scope_pipeline_reviews_repaired_quotes_and_keeps_original_refusal_for_audit()
    {
        var (source, _) = Fixture();
        var unit = source.Units[0];
        var proposal = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] {
            new { unitId = unit.Id, quote = "missing label", role = "label" }, new { unitId = unit.Id, quote = "Sở A", role = "value" } } } } });
        var repair = System.Text.Json.JsonSerializer.Serialize(new { repairs = new[] { new { index = 0, decision = "REPLACE", reason = "literal unit text",
            parts = new[] { new { unitId = unit.Id, quote = unit.Text, role = "text" } } } } });
        var reviewer = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"supported\"}]}");
        var onlyFirst = source with { Units = [unit] };
        var result = await GatherScopePipeline.RunAsync("Mục", "Mục", "", null, [onlyFirst], new ProposalAndRepairModel(proposal, repair), reviewer);
        Assert.Single(result.Selection.Refused);
        Assert.Empty(result.Repair.Remaining);
        Assert.Equal("REPAIRED", Assert.Single(result.Repair.Decisions).Status);
        Assert.Equal(unit.Text, Assert.Single(result.ReviewedSection.Run.Facts).Render());
        Assert.Empty(result.ReviewedSection.Run.Refused);
    }

    [Fact]
    public async Task Invalid_first_quote_repair_gets_one_targeted_retry_with_validation_feedback()
    {
        var (source, _) = Fixture();
        var unit = source.Units[0];
        var proposal = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] { new { unitId = unit.Id, quote = "not in source", role = "text" } } } } });
        static string Repair(string unitId, string quote) => System.Text.Json.JsonSerializer.Serialize(new { repairs = new[] {
            new { index = 0, decision = "REPLACE", reason = "repair", parts = new[] { new { unitId, quote, role = "text" } } } } });
        var proposer = new RetryRepairModel(proposal, Repair(unit.Id, "still not in source"), Repair(unit.Id, unit.Text));
        var reviewer = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"supported\"}]}");
        var result = await GatherScopePipeline.RunAsync("Mục", "Mục", "", null, [source with { Units = [unit] }], proposer, reviewer);
        Assert.Equal(2, proposer.RepairCalls);
        Assert.True(proposer.SawFeedback);
        Assert.Equal(["UNRESOLVED", "REPAIRED"], result.Repair.Decisions.Select(d => d.Status));
        Assert.Empty(result.Repair.Remaining);
        Assert.Equal(unit.Text, Assert.Single(result.ReviewedSection.Run.Facts).Render());
    }

    [Fact]
    public async Task Explicit_unsure_repair_is_not_retried_as_a_protocol_failure()
    {
        var (source, _) = Fixture();
        var unit = source.Units[0];
        var proposal = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] { new { unitId = unit.Id, quote = "not in source", role = "text" } } } } });
        const string unsure = "{\"repairs\":[{\"index\":0,\"decision\":\"UNSURE\",\"reason\":\"not supported\",\"parts\":[]}]}";
        var proposer = new RetryRepairModel(proposal, unsure, "{}");
        // Coverage excludes the unit, so nothing resolves the refusal.
        var reviewer = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"EXCLUDE\",\"reason\":\"not relevant\"}]}");
        var result = await GatherScopePipeline.RunAsync("Mục", "Mục", "", null, [source with { Units = [unit] }], proposer, reviewer);
        Assert.Equal(1, proposer.RepairCalls);
        Assert.StartsWith("MODEL_UNSURE:", Assert.Single(result.Repair.Decisions).Reason);
        Assert.Single(result.Repair.Remaining);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task Nonretryable_budget_failure_is_not_retried_by_quote_repair()
    {
        var (source, _) = Fixture();
        var unit = source.Units[0];
        var proposal = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] { new { unitId = unit.Id, quote = "not in source", role = "text" } } } } });
        var proposer = new NoRetryRepairModel(proposal);
        // Coverage excludes the unit, so nothing resolves the refusal.
        var reviewer = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"EXCLUDE\",\"reason\":\"not relevant\"}]}");
        var result = await GatherScopePipeline.RunAsync("Mục", "Mục", "", null, [source with { Units = [unit] }], proposer, reviewer);
        Assert.Equal(1, proposer.RepairCalls);
        Assert.StartsWith("REPAIR_NONRETRYABLE:", Assert.Single(result.Repair.Decisions).Reason);
        Assert.Single(result.Repair.Remaining);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task A_kept_whole_source_unit_resolves_a_quote_refusal()
    {
        var (source, _) = Fixture();
        var selected = source.Units[0];
        var proposal = System.Text.Json.JsonSerializer.Serialize(new { facts = new[] { new { parts = new[] {
            new { unitId = selected.Id, quote = "model copied this incorrectly", role = "text" } } } } });
        const string unsure = "{\"repairs\":[{\"index\":0,\"decision\":\"UNSURE\",\"reason\":\"cannot bind quote\",\"parts\":[]}]}";
        var proposer = new RetryRepairModel(proposal, unsure, "{}");
        var reviewer = new Model("{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"relevant unit\"},{\"id\":1,\"decision\":\"KEEP\",\"reason\":\"relevant unit\"}]}");
        var result = await GatherScopePipeline.RunAsync("Thông tin chung", "Mục", "body[1]/p[1]", null,
            [source], proposer, reviewer, reviewBatchSize: 10, reviewConcurrency: 1);

        Assert.Contains(0, result.ResolvedRefusalIndices);
        Assert.Single(result.Repair.Remaining);
        Assert.Empty(result.ReviewedSection.Run.Refused);
        var recovered = Assert.Single(result.ReviewedSection.Run.Facts.Where(f => f.Parts.Any(p => p.UnitId == selected.Id)));
        Assert.Equal(selected.Text, recovered.Render());
        var part = Assert.Single(recovered.Parts);
        Assert.Equal(selected.Text, part.Text);
        Assert.Equal(0, part.Start);
        Assert.Equal(selected.Text.Length, part.End);
        Assert.DoesNotContain("SCOPE_UNREVIEWED", recovered.Flags);
        Assert.True(result.Complete);
    }
}
