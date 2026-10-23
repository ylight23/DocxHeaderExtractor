using System.Text;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The user instruction of the frozen D4.12 method, verbatim, read from its fixture. It is test data, not production code: the parity tests
/// compare the production prompts against the text the qualified run used, and the fixture's SHA-256 is pinned in PdfLaneTests.
/// </summary>
internal static class D412Prompt
{
    public static string Text { get; } = File.ReadAllText(
        TestRepository.Path("tests/DocxHeaderExtractor.Tests/Golden/d412-instruction.txt"), new UTF8Encoding(false));
}
