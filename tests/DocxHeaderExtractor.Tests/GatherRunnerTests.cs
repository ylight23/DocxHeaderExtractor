using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The gathering chain without a real model: the reader, the outline matching, the batches, the prompt, the reply parsing and the checker, with a
/// scripted model. The fixture is a small DOCX with a heading that runs over two paragraphs and a heading whose text appears twice.
/// </summary>
public sealed class GatherRunnerTests
{
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static HeadingRecord Heading(int index, int? level, string text, string stableId) =>
        new() { Index = index, Level = level, Text = text, StableId = stableId };

    /// <summary>Paragraphs: 1 "Phạm vi" (heading), 2 "Dự án ở Hà Nội.", 3-4 one heading split over two paragraphs, 5 a bullet, 6 "Phạm vi" again, 7 a bullet.</summary>
    static string Fixture() =>
        P("Phạm vi") + P("Dự án ở Hà Nội.") + P("Thông tin") + P("chung của dự án") + P("- Chủ trì: Sở A.") + P("Phạm vi") + P("- Thời gian: 2027.");

    static readonly HeadingRecord[] Outline =
    [
        Heading(0, 1, "Phạm vi", "body[1]/p[1]"),
        Heading(2, 1, "Thông tin chung của dự án", "body[1]/p[3]"),
        Heading(5, 1, "Phạm vi", "body[1]/p[6]"),
    ];

    static GatherSource Read(string body, IReadOnlyList<HeadingRecord>? outline = null)
    {
        var path = RawDocxFixtureTests.Build(body);
        try { return GatherUnits.Read(path, "src", outline); }
        finally { File.Delete(path); }
    }

    /// <summary>A model that answers each call with the next scripted reply; a reply that throws is an exception from the model.</summary>
    sealed class ScriptedModel(params Func<string, string>[] replies) : IGatherModel
    {
        int next;
        public List<string> Users { get; } = [];

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            Users.Add(user);
            return Task.FromResult(replies[Math.Min(next++, replies.Length - 1)](user));
        }
    }

    [Fact]
    public void A_heading_split_over_two_paragraphs_is_matched_as_one_heading_when_its_joined_text_is_the_heading()
    {
        var source = Read(Fixture(), [
            Heading(0, 1, "Phạm vi", "body[1]/p[1]"),
            Heading(2, 1, "Thông tin chung của dự án", "body[1]/p[3]"),
            Heading(5, 1, "Phạm vi", "body[1]/p[6]"),
        ]);

        Assert.Contains(source.Gaps, g => g.Reason == "heading_spans_paragraphs:2");
        Assert.DoesNotContain(source.Gaps, g => g.Reason.StartsWith("heading_unmatched", StringComparison.Ordinal));
        var first = source.Units.Single(u => u.Text == "Thông tin");
        var second = source.Units.Single(u => u.Text == "chung của dự án");
        Assert.True(first.IsHeading);
        Assert.True(second.IsHeading);
        var bullet = source.Units.Single(u => u.Text == "- Chủ trì: Sở A.");
        Assert.True(bullet.PathCertain);
        Assert.Equal("Thông tin chung của dự án", bullet.HeadingPath);
        Assert.Equal("Phạm vi", source.Units.Single(u => u.Text == "Dự án ở Hà Nội.").HeadingPath);
    }

    [Fact]
    public void A_heading_whose_joined_text_does_not_match_the_file_stays_a_gap_and_its_units_are_uncertain()
    {
        var source = Read(Fixture(), [
            Heading(0, 1, "Phạm vi", "body[1]/p[1]"),
            Heading(2, 1, "Thông tin khác hẳn", "body[1]/p[3]"),
            Heading(5, 1, "Phạm vi", "body[1]/p[6]"),
        ]);

        Assert.Contains(source.Gaps, g => g.Reason == "heading_unmatched:2");
        Assert.False(source.Units.Single(u => u.Text == "- Chủ trì: Sở A.").PathCertain);
    }

    [Fact]
    public void A_heading_whose_text_appears_twice_is_matched_at_each_occurrence()
    {
        var source = Read(Fixture(), Outline);

        var headings = source.Units.Where(u => u.IsHeading && u.Text == "Phạm vi").Select(u => u.Location).ToList();
        Assert.Equal(["body[1]/p[1]", "body[1]/p[6]"], headings);
        Assert.DoesNotContain(source.Gaps, g => g.Reason.StartsWith("heading_unmatched:0", StringComparison.Ordinal) || g.Reason == "heading_unmatched:5");
    }

    [Fact]
    public async Task A_valid_reply_keeps_its_span_with_the_source_text_and_the_path()
    {
        var source = Read(Fixture(), Outline);
        var model = new ScriptedModel(_ => "{\"facts\":[{\"parts\":[{\"unitId\":\"src|body[1]/p[7]\",\"quote\":\"2027\",\"role\":\"text\"}]}]}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        var fact = Assert.Single(run.Facts);
        Assert.Equal("2027", fact.Parts[0].Text);
        Assert.Equal(13, fact.Parts[0].Start);
        Assert.Equal(17, fact.Parts[0].End);
        Assert.Equal("body[1]/p[7]", fact.Parts[0].Location);
        Assert.Empty(run.BatchFailures);
        Assert.Empty(run.Refused);
    }

    [Fact]
    public async Task Model_quotes_are_resolved_to_utf16_offsets_and_ambiguous_or_changed_text_is_refused()
    {
        var source = Read(P("😀 Sở A và Sở A; năm 2027."));
        var id = source.Units.Single().Id;
        var model = new ScriptedModel(_ => "{\"facts\":["
            + "{\"parts\":[{\"unitId\":\"" + id + "\",\"quote\":\"2027\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"" + id + "\",\"quote\":\"Sở A\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"" + id + "\",\"quote\":\"sở a\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"" + id + "\",\"start\":0,\"end\":2,\"role\":\"text\"}]}]}");
        var run = await GatherRunner.RunAsync("Thông tin", [source], model);
        var part = Assert.Single(run.Facts).Parts[0];
        Assert.Equal(21, part.Start);
        Assert.Equal(source.Units[0].Text.IndexOf("2027", StringComparison.Ordinal), part.Start);
        Assert.Equal(part.Start + 4, part.End);
        Assert.Equal(["QUOTE_AMBIGUOUS", "QUOTE_NOT_FOUND", "QUOTE_MISSING"], run.Refused.Select(r => r.Reason));
        Assert.Equal("Sở A", run.Refused[0].Proposal!.Parts[0].Quote);
        Assert.False(run.Complete);
    }

    [Fact]
    public async Task Duplicate_points_to_kept_fact_even_after_an_earlier_refusal()
    {
        var source = Read(P("Sở A") + P("Sở A"));
        var a = source.Units[0].Id;
        var b = source.Units[1].Id;
        var model = new ScriptedModel(_ => "{\"facts\":["
            + "{\"parts\":[{\"unitId\":\"" + a + "\",\"quote\":\"không có\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"" + a + "\",\"quote\":\"Sở A\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"" + b + "\",\"quote\":\"Sở A\",\"role\":\"text\"}]}]}");
        var run = await GatherRunner.RunAsync("Thông tin", [source], model);
        var duplicate = Assert.Single(run.Duplicates);
        Assert.Equal(0, duplicate.KeptIndex);
        Assert.Equal(2, duplicate.DuplicateIndex);
        Assert.Equal("Sở A", run.Facts[duplicate.KeptIndex].Parts[0].Text);
    }

    [Fact]
    public async Task Multiple_values_and_multiple_units_are_flagged_without_changing_the_source_quotes()
    {
        var source = Read(P("Sở A") + P("Sở B"));
        var a = source.Units[0].Id;
        var b = source.Units[1].Id;
        var model = new ScriptedModel(_ => "{\"facts\":[{\"parts\":["
            + "{\"unitId\":\"" + a + "\",\"quote\":\"Sở A\",\"role\":\"value\"},"
            + "{\"unitId\":\"" + b + "\",\"quote\":\"Sở B\",\"role\":\"value\"}]}]}");
        var run = await GatherRunner.RunAsync("Thông tin", [source], model);
        var fact = Assert.Single(run.Facts);
        Assert.Contains("MULTIPLE_UNITS", fact.Flags);
        Assert.Contains("MULTIPLE_VALUES", fact.Flags);
        Assert.Equal(["Sở A", "Sở B"], fact.Parts.Select(p => p.Text));
    }

    [Fact]
    public async Task The_prompt_carries_the_goal_and_the_unit_ids_the_model_may_cite()
    {
        var source = Read(Fixture(), Outline);
        var model = new ScriptedModel(_ => "{\"facts\":[]}");

        await GatherRunner.RunAsync("Thông tin chung", [source], model);

        var user = Assert.Single(model.Users);
        Assert.StartsWith("Goal: Thông tin chung", user, StringComparison.Ordinal);
        Assert.Contains("\"unitId\":\"src|body[1]/p[7]\"", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_span_outside_its_unit_and_an_unknown_unit_are_refused_with_reasons()
    {
        var source = Read(Fixture(), Outline);
        var model = new ScriptedModel(_ => "{\"facts\":["
            + "{\"parts\":[{\"unitId\":\"src|body[1]/p[2]\",\"quote\":\"not present\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"src|body[1]/p[99]\",\"quote\":\"x\",\"role\":\"text\"}]}]}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        Assert.Empty(run.Facts);
        Assert.Equal(["QUOTE_NOT_FOUND", "UNKNOWN_UNIT"], run.Refused.Select(r => r.Reason));
        Assert.False(run.Complete);
    }

    [Fact]
    public async Task A_batch_whose_replies_stay_bad_is_recorded_and_its_units_are_listed_as_unreviewed()
    {
        var source = Read(Fixture(), Outline);
        var model = new ScriptedModel(_ => "không phải JSON", _ => "{\"other\":1}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        Assert.Equal(GatherRunner.MaxAttempts, model.Users.Count);
        Assert.Equal(["batch-001:NO_FACTS_ARRAY"], run.BatchFailures);
        Assert.False(run.Complete);
        Assert.Equal(source.Units.Select(u => u.Id), run.UnreviewedUnitIds);
        Assert.Empty(run.Facts);
    }

    [Fact]
    public async Task A_bad_reply_followed_by_a_good_one_is_retried_and_the_run_is_complete()
    {
        var source = Read(Fixture(), Outline);
        var model = new ScriptedModel(_ => "không phải JSON", _ => "{\"facts\":[{\"parts\":[{\"unitId\":\"src|body[1]/p[7]\",\"quote\":\"2027\",\"role\":\"text\"}]}]}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        Assert.Equal(2, model.Users.Count);
        Assert.True(run.Complete);
        Assert.Empty(run.UnreviewedUnitIds);
        Assert.Equal("2027", Assert.Single(run.Facts).Parts[0].Text);
    }

    [Fact]
    public async Task A_model_call_that_throws_is_recorded_and_the_gaps_still_reach_the_result()
    {
        var source = Read(Fixture(), Outline);
        var model = new ScriptedModel(_ => throw new InvalidOperationException("boom"));

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        Assert.Equal(["batch-001:MODEL_CALL_FAILED:InvalidOperationException"], run.BatchFailures);
        Assert.False(run.Complete);
        Assert.Contains(run.Gaps, g => g.Reason == "heading_spans_paragraphs:2");
    }

    [Fact]
    public async Task A_fact_citing_a_unit_the_model_was_not_shown_in_its_batch_is_refused_as_unseen()
    {
        // The long paragraph does not fit in the first batch, so it is sent alone in the second one.
        var source = Read(P("Đoạn đầu.") + P(new string('x', GatherBatches.MaxChars)));
        var longId = source.Units.Single(u => u.Text.StartsWith("xxx", StringComparison.Ordinal)).Id;
        var model = new ScriptedModel(
            _ => "{\"facts\":[{\"parts\":[{\"unitId\":\"" + longId + "\",\"quote\":\"xxxxx\",\"role\":\"text\"}]}]}",
            _ => "{\"facts\":[]}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        Assert.Equal(2, run.Batches);
        Assert.Equal(["UNSEEN_UNIT"], run.Refused.Select(r => r.Reason));
        Assert.Empty(run.Facts);
    }

    [Fact]
    public async Task A_label_and_value_in_different_batches_are_refused_as_one_fact_because_the_model_never_saw_both()
    {
        var source = Read(P("Tên dự án") + P(new string('x', GatherBatches.MaxChars)));
        var label = source.Units.Single(u => u.Text == "Tên dự án");
        var value = source.Units.Single(u => u.Text.StartsWith("xxx", StringComparison.Ordinal));
        var model = new ScriptedModel(
            _ => "{\"facts\":[{\"parts\":["
                + "{\"unitId\":\"" + label.Id + "\",\"quote\":\"Tên dự án\",\"role\":\"label\"},"
                + "{\"unitId\":\"" + value.Id + "\",\"quote\":\"xxxxx\",\"role\":\"value\"}]}]}",
            _ => "{\"facts\":[]}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [source], model);

        Assert.Equal(2, run.Batches);
        Assert.Equal(["UNSEEN_UNIT"], run.Refused.Select(r => r.Reason));
        Assert.Empty(run.Facts);
    }

    [Fact]
    public async Task Facts_from_two_sources_with_the_same_text_are_kept_once_and_both_sources_are_named()
    {
        var a = Read(P("Sở A chủ trì."));
        var b = Read(P("Sở A chủ trì."));
        var idA = a.Units.Single().Id.Replace("src|", "a|", StringComparison.Ordinal);
        var idB = b.Units.Single().Id;
        var a2 = new GatherSource(a.Units.Select(u => u with { Id = idA, SourceId = "a" }).ToList(), a.Gaps);
        var b2 = new GatherSource(b.Units.Select(u => u with { SourceId = "b" }).ToList(), b.Gaps);
        var model = new ScriptedModel(_ => "{\"facts\":["
            + "{\"parts\":[{\"unitId\":\"" + idA + "\",\"quote\":\"Sở A\",\"role\":\"text\"}]},"
            + "{\"parts\":[{\"unitId\":\"" + idB + "\",\"quote\":\"Sở A\",\"role\":\"text\"}]}]}");

        var run = await GatherRunner.RunAsync("Thông tin chung", [a2, b2], model);

        Assert.Single(run.Facts);
        var duplicate = Assert.Single(run.Duplicates);
        Assert.Equal("b", Assert.Single(duplicate.Parts).SourceId);
    }

    [Fact]
    public async Task Truncated_batch_is_split_and_both_halves_are_reviewed()
    {
        var source = Read(P("Một") + P("Hai"));
        var model = new ScriptedModel(_ => throw new GatherCompletionTruncatedException(), _ => "{\"facts\":[]}");
        var run = await GatherRunner.RunAsync("Thông tin", [source], model);
        Assert.True(run.Complete);
        Assert.Equal(3, model.Users.Count);
        Assert.Equal(2, run.Batches);
        Assert.Contains(source.Units[0].Id, model.Users[1]);
        Assert.DoesNotContain(source.Units[1].Id, model.Users[1]);
        Assert.Contains(source.Units[1].Id, model.Users[2]);
    }

    [Fact]
    public async Task Truncated_single_unit_remains_unreviewed_without_retrying_the_same_packet()
    {
        var source = Read(P("Một"));
        var model = new ScriptedModel(_ => throw new GatherCompletionTruncatedException());
        var run = await GatherRunner.RunAsync("Thông tin", [source], model);
        Assert.False(run.Complete);
        Assert.Single(model.Users);
        Assert.Equal(["batch-001:FINISH_LENGTH"], run.BatchFailures);
        Assert.Equal(source.Units.Select(u => u.Id), run.UnreviewedUnitIds);
    }

    [Fact]
    public async Task Checkpoints_resume_only_identical_prompt_and_model_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-gather-checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Read(P("Một") + P(new string('x', GatherBatches.MaxChars)));
            var store = new GatherCheckpointStore(directory, "provider:model:settings-v1");
            var interrupted = new ScriptedModel(_ => "{\"facts\":[]}", _ => throw new OperationCanceledException());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatherRunner.RunAsync("Mục A", [source], interrupted, checkpoints: store));
            var resumed = new ScriptedModel(_ => "{\"facts\":[]}");
            var result = await GatherRunner.RunAsync("Mục A", [source], resumed, checkpoints: store);
            Assert.True(result.Complete);
            Assert.Single(resumed.Users);
            var other = new ScriptedModel(_ => "{\"facts\":[]}");
            await GatherRunner.RunAsync("Mục B", [source], other, checkpoints: store);
            Assert.Equal(2, other.Users.Count);
            var differentModel = new ScriptedModel(_ => "{\"facts\":[]}");
            await GatherRunner.RunAsync("Mục A", [source], differentModel, checkpoints: new GatherCheckpointStore(directory, "provider:model:settings-v2"));
            Assert.Equal(2, differentModel.Users.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Resume_remembers_the_split_and_does_not_resend_the_truncated_parent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-gather-split-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Read(P("Một") + P("Hai"));
            var store = new GatherCheckpointStore(directory, "model-v1");
            var first = new ScriptedModel(_ => throw new GatherCompletionTruncatedException(), _ => "{\"facts\":[]}", _ => throw new OperationCanceledException());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatherRunner.RunAsync("Mục", [source], first, checkpoints: store));
            var resumed = new ScriptedModel(_ => "{\"facts\":[]}");
            var result = await GatherRunner.RunAsync("Mục", [source], resumed, checkpoints: store);
            Assert.True(result.Complete);
            Assert.Single(resumed.Users);
            Assert.Contains(source.Units[1].Id, resumed.Users[0]);
            Assert.DoesNotContain(source.Units[0].Id, resumed.Users[0]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Corrupt_checkpoint_fails_without_a_new_provider_call()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-gather-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Read(P("Một"));
            var store = new GatherCheckpointStore(directory, "model-v1");
            await GatherRunner.RunAsync("Mục", [source], new ScriptedModel(_ => "{\"facts\":[]}"), checkpoints: store);
            File.WriteAllText(Assert.Single(Directory.GetFiles(directory, "*.json")), "bad json");
            var model = new ScriptedModel(_ => "{\"facts\":[]}");
            await Assert.ThrowsAsync<InvalidDataException>(() => GatherRunner.RunAsync("Mục", [source], model, checkpoints: store));
            Assert.Empty(model.Users);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
