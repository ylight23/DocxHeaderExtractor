using Microsoft.AspNetCore.Http;
using DocxHeaderExtractor.Web;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99DocumentFirstReviewTests
{
    [Fact]
    public async Task Mau_docx_supports_source_session_draft_resume_validation_freeze_and_compare()
    {
        var root = FindRepositoryRoot();
        var work = Path.Combine(Path.GetTempPath(), "dhx-accuracy99-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DHX_ACCURACY99_WORK_DIR");
        Environment.SetEnvironmentVariable("DHX_ACCURACY99_WORK_DIR", work);
        try
        {
            await using var stream = File.OpenRead(Path.Combine(root, "samples", "mau.docx"));
            var upload = new FormFile(stream, 0, stream.Length, "file", "mau.docx");
            var store = new Accuracy99DocumentFirstReview();
            var created = await store.CreateOrResumeAsync(upload, CancellationToken.None);

            Assert.Equal(19, created.Packet.Occurrences.Count);
            Assert.False(created.Packet.Manifest["predictionsIncluded"]!.GetValue<bool>());
            Assert.Equal("REVIEW_DRAFT", created.Packet.Manifest["reviewStatus"]!.GetValue<string>());
            Assert.All(created.Packet.Occurrences, row => Assert.Null(row["adjudicatedLabel"]));

            var heading = created.Packet.Occurrences.First(row => !string.IsNullOrWhiteSpace(row["rawSourceText"]?.GetValue<string>()));
            var headingText = heading["rawSourceText"]!.GetValue<string>();
            heading["adjudicatedLabel"] = "HEADING";
            heading["headingStart"] = 0;
            heading["headingEnd"] = headingText.Length;
            heading["headingText"] = headingText;
            heading["structuralType"] = "Heading";
            heading["level"] = 1;
            heading["levelReviewStatus"] = "REVIEWED";
            heading["parentReviewStatus"] = "ROOT";
            heading["goldHeadingId"] = GoldHeadingId(heading["documentId"]!.GetValue<string>(), heading["sourceId"]!.GetValue<string>(), 0, headingText.Length);
            heading["reviewer"] = "synthetic-test";
            foreach (var row in created.Packet.Occurrences.Where(row => !ReferenceEquals(row, heading)))
            {
                row["adjudicatedLabel"] = "NON_HEADING";
                row["reviewer"] = "synthetic-test";
            }

            await store.SaveDraftAsync(created.Id, Accuracy99DocumentFirstReview.ToJsonLines(created.Packet), CancellationToken.None);
            var resumed = store.Load(created.Id);
            Assert.Equal("NON_HEADING", resumed.Packet.Occurrences[1]["adjudicatedLabel"]!.GetValue<string>());
            Assert.Empty(store.Validate(created.Id));

            var frozen = await store.FreezeAsync(created.Id, CancellationToken.None);
            Assert.True(frozen.Frozen);
            Assert.Equal("GOLD_FROZEN", frozen.Packet.Manifest["reviewStatus"]!.GetValue<string>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveDraftAsync(created.Id, Accuracy99DocumentFirstReview.ToJsonLines(created.Packet), CancellationToken.None));

            var comparison = await store.CompareAsync(created.Id, CancellationToken.None);
            Assert.Equal(0, comparison.GetType().GetProperty("providerCalls")!.GetValue(comparison));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DHX_ACCURACY99_WORK_DIR", previous);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static string GoldHeadingId(string documentId, string sourceId, int start, int end)
    {
        static void Append(System.Text.StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append('|');
        var frame = new System.Text.StringBuilder();
        Append(frame, documentId); Append(frame, sourceId); Append(frame, start.ToString(System.Globalization.CultureInfo.InvariantCulture)); Append(frame, end.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "gold-heading:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(frame.ToString()))).ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
