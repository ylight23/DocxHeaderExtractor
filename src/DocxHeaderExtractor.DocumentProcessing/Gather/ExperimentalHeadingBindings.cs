using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Preflight evidence only: verifies captured source parts against the exact old source. It does not verify outline completeness.</summary>
public static class ExperimentalHeadingBindings
{
    public const string Version = "EXPERIMENTAL_HEADING_BINDINGS_V1";
    public sealed record Part(string SourceId, int Ordinal, int Start, int End, string SourceTextSha256);
    public sealed record Binding(string HeadingId, int Index, IReadOnlyList<Part> Parts);
    public sealed record Snapshot(string Version, string SourceSha256, IReadOnlyList<Binding> Headings);
    public sealed record Result(bool Complete, IReadOnlyList<string> Problems);

    static string TextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static bool SplitsSurrogate(string value, int at) => at > 0 && at < value.Length && char.IsHighSurrogate(value[at - 1]) && char.IsLowSurrogate(value[at]);
    static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public static Result Check(IReadOnlyList<HeadingRecord> headings, IReadOnlyDictionary<string, string> sourceTexts)
    {
        var problems = new List<string>();
        if (headings.Count == 0) problems.Add("NO_HEADINGS_TO_VERIFY");
        foreach (var h in headings)
        {
            var id = h.StableId ?? "index:" + h.Index;
            if (h.ValidatedSourcePartCount <= 0 || h.SourceParts is null || h.SourceParts.Count != h.ValidatedSourcePartCount)
            { problems.Add(id + ":SOURCE_PARTS_MISSING_OR_INCOMPLETE"); continue; }
            foreach (var part in h.SourceParts)
            {
                if (!sourceTexts.TryGetValue(part.SourceId, out var text))
                { problems.Add(id + ":SOURCE_NOT_FOUND:" + part.SourceId); continue; }
                if (!StringComparer.Ordinal.Equals(text, part.OriginalText))
                { problems.Add(id + ":SOURCE_TEXT_CHANGED:" + part.SourceId); continue; }
                if (part.Span.Start < 0 || part.Span.End <= part.Span.Start || part.Span.End > text.Length ||
                    SplitsSurrogate(text, part.Span.Start) || SplitsSurrogate(text, part.Span.End))
                    problems.Add(id + ":INVALID_UTF16_SPAN:" + part.SourceId);
            }
        }
        return new(problems.Count == 0, problems);
    }

    /// <summary>Captures evidence from the live, fully validated projection only. No snapshot is returned on any missing part.</summary>
    public static Snapshot? Capture(string sourceSha256, IReadOnlyList<HeadingRecord> headings, IReadOnlyDictionary<string, string> sourceTexts)
    {
        if (!IsSha256(sourceSha256) || !Check(headings, sourceTexts).Complete) return null;
        return new(Version, sourceSha256, headings.Select(h => new Binding(h.StableId ?? "index:" + h.Index, h.Index,
            h.SourceParts!.Select(p => new Part(p.SourceId, p.SourceOrdinal, p.Span.Start, p.Span.End, TextHash(p.OriginalText))).ToArray())).ToArray());
    }

    /// <summary>Checks a snapshot against its original file identity and exact source text; this is not cross-version mapping.</summary>
    public static Result Verify(Snapshot? snapshot, string sourceSha256, IReadOnlyDictionary<string, string> sourceTexts)
    {
        var problems = new List<string>();
        if (snapshot is null || snapshot.Version != Version || !IsSha256(sourceSha256) || !StringComparer.Ordinal.Equals(snapshot.SourceSha256, sourceSha256) || snapshot.Headings.Count == 0)
            return new(false, ["SNAPSHOT_IDENTITY_OR_VERSION_INVALID"]);
        foreach (var heading in snapshot.Headings)
        {
            if (heading.Parts.Count == 0) { problems.Add(heading.HeadingId + ":SOURCE_PARTS_MISSING"); continue; }
            foreach (var part in heading.Parts)
            {
                if (!sourceTexts.TryGetValue(part.SourceId, out var text) || !StringComparer.Ordinal.Equals(TextHash(text), part.SourceTextSha256))
                { problems.Add(heading.HeadingId + ":SOURCE_MISSING_OR_CHANGED:" + part.SourceId); continue; }
                if (part.Start < 0 || part.End <= part.Start || part.End > text.Length || SplitsSurrogate(text, part.Start) || SplitsSurrogate(text, part.End))
                    problems.Add(heading.HeadingId + ":INVALID_UTF16_SPAN:" + part.SourceId);
            }
        }
        return new(problems.Count == 0, problems);
    }
}
