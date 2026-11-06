using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Sources.Docx.Changes;

/// <summary>
/// One change Word recorded with Track Changes, as written in the file: its kind, the paragraph it is in (the same location as the paragraph
/// fingerprints), who made it and when, and its text. Kinds: insert, delete, move_from (text moved away), move_to (text moved here),
/// format_run (run formatting changed), format_paragraph (paragraph properties changed), paragraph_inserted / paragraph_deleted (the
/// paragraph mark itself, so a paragraph was added, or merged with the next). It records what the author did since the version the
/// tracking started from, which is not necessarily the version this file is compared with.
/// </summary>
public sealed record TrackedRevision(string Kind, string Location, string? Author, string? Date, string Text);

/// <summary>Reads the tracked changes of the paragraphs the walker gives, in reading order. Deterministic; changes nothing.</summary>
public static class TrackedRevisions
{
    public static IReadOnlyList<TrackedRevision> Read(IEnumerable<(string Location, Paragraph Paragraph)> paragraphs)
    {
        var result = new List<TrackedRevision>();
        foreach (var (location, paragraph) in paragraphs)
        {
            bool Own(OpenXmlElement e) => ReferenceEquals(e.Ancestors<Paragraph>().FirstOrDefault(), paragraph);
            if (paragraph.ParagraphProperties?.ParagraphMarkRunProperties is { } mark)
            {
                if (mark.GetFirstChild<Inserted>() is { } inserted) result.Add(Revision("paragraph_inserted", location, inserted, Text(paragraph)));
                if (mark.GetFirstChild<Deleted>() is { } deleted) result.Add(Revision("paragraph_deleted", location, deleted, Text(paragraph)));
            }
            if (paragraph.ParagraphProperties?.GetFirstChild<ParagraphPropertiesChange>() is { } paragraphChange)
                result.Add(Revision("format_paragraph", location, paragraphChange, Text(paragraph)));
            foreach (var element in paragraph.Descendants().Where(Own))
            {
                switch (element)
                {
                    case InsertedRun ins: result.Add(Revision("insert", location, ins, Text(ins))); break;
                    case DeletedRun del: result.Add(Revision("delete", location, del, Text(del))); break;
                    case MoveFromRun from: result.Add(Revision("move_from", location, from, Text(from))); break;
                    case MoveToRun to: result.Add(Revision("move_to", location, to, Text(to))); break;
                    case RunPropertiesChange change when change.Parent?.Parent is Run run: result.Add(Revision("format_run", location, change, Text(run))); break;
                }
            }
        }
        return result;
    }

    static TrackedRevision Revision(string kind, string location, OpenXmlElement element, string text) =>
        new(kind, location, Attr(element, "author"), Attr(element, "date"), text);

    static string? Attr(OpenXmlElement element, string name) =>
        element.GetAttributes().FirstOrDefault(a => a.LocalName == name).Value is { Length: > 0 } value ? value : null;

    /// <summary>The text an element holds, deleted text included (a deletion's text is what was removed).</summary>
    static string Text(OpenXmlElement element) =>
        string.Concat(element.Descendants().Select(e => e switch { Text t => t.Text, DeletedText d => d.Text, TabChar => "\t", _ => "" }));
}
