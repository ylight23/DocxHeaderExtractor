using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The locator's projection from a reduced match back onto source offsets.
/// <para>
/// Punctuation-insensitive matching drops characters to find a heading whose recorded wording lost
/// some of them. What it must not do is let that reduction reach the coordinates: the parts it
/// returns quote the source, and a heading that ends in ')' ends in ')' in the source whether or
/// not the search ignored it. It did reach the coordinates, for every DOC-0252 heading whose
/// wording ended in punctuation, and the migrated Gold recorded selections one character short of
/// their own approved text.
/// </para>
/// <para>
/// The invariant these pin: a target that accounts for a whole atom is located as that whole atom -
/// never as <c>0..Length-1</c>.
/// </para>
/// </summary>
public sealed class StructuredSourcePartLocatorTests
{
    [Theory]
    // The four shapes named in the authorization for the Gold correction, plus the apostrophe form
    // that made one of them easy to misread as a character-set problem rather than a boundary one.
    [InlineData("ABC (XYZ)")]
    [InlineData("[ABC]")]
    [InlineData("Heading.")]
    [InlineData("Section:")]
    [InlineData("SESSION V: Current Research (Cont’d)")]
    [InlineData("Commonwealth of Independent States (CIS)")]
    public void A_target_that_is_a_whole_atom_is_located_as_that_whole_atom(string text)
    {
        var atoms = Atoms(text);
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(atoms, text, punctuationInsensitive: true, ref cursor);

        var part = Assert.Single(parts!);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode);
        Assert.Null(part.VerbatimText);
        Assert.Equal(text, Render(atoms, parts!));
    }

    [Theory]
    [InlineData("ABC (XYZ)", ")")]
    [InlineData("Heading.", ".")]
    [InlineData("Section:", ":")]
    [InlineData("Quote?", "?")]
    public void Terminal_punctuation_survives_the_reduced_match(string text, string terminal)
    {
        // The defect in one assertion: the selection used to stop at the last character that
        // survived reduction, which for these is the one before the punctuation.
        var atoms = Atoms(text + " and then body text that is not part of it");
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(atoms, text, punctuationInsensitive: true, ref cursor);

        var part = Assert.Single(parts!);
        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, part.SelectionMode);
        Assert.EndsWith(terminal, part.VerbatimText!, StringComparison.Ordinal);
        Assert.Equal(text, part.VerbatimText);
    }

    [Fact]
    public void Leading_punctuation_survives_it_too()
    {
        // The same defect at the other edge, which the four DOC-0252 claims never exercised.
        var atoms = Atoms("[ABC] followed by prose");
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(atoms, "[ABC]", punctuationInsensitive: true, ref cursor);

        Assert.Equal("[ABC]", Assert.Single(parts!).VerbatimText);
    }

    [Fact]
    public void A_wording_missing_interior_punctuation_still_matches_and_still_keeps_the_source_tail()
    {
        // Why the punctuation-insensitive branch exists at all, and the case that must keep working:
        // the legacy line reconstruction dropped the '.' after an ordinal, so Gold's recorded wording
        // and the source differ in the middle. The match must still be found, and the source's own
        // terminal ')' must still be selected.
        var atoms = Atoms("3. Treatment (ECP)");
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(atoms, "3 Treatment (ECP)", punctuationInsensitive: true, ref cursor);

        var part = Assert.Single(parts!);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode);
        Assert.Equal("3. Treatment (ECP)", Render(atoms, parts!));
    }

    [Fact]
    public void A_heading_that_wraps_across_atoms_keeps_the_tail_on_its_last_atom()
    {
        var atoms = Atoms("A Survey Based Approach to Adjustment", "for Quality Differences (QD)");
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(
            atoms, "A Survey Based Approach to Adjustment for Quality Differences (QD)",
            punctuationInsensitive: true, ref cursor);

        Assert.Equal(2, parts!.Count);
        Assert.All(parts, part => Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode));
    }

    [Fact]
    public void An_exact_match_is_unaffected_by_any_of_this()
    {
        // The punctuation-insensitive path is the one that was wrong. The exact path never reduced
        // anything and must return exactly what it always did.
        var atoms = Atoms("Heading (X) and more");
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(atoms, "Heading (X)", punctuationInsensitive: false, ref cursor);

        Assert.Equal("Heading (X)", Assert.Single(parts!).VerbatimText);
    }

    [Fact]
    public void Text_beyond_the_target_is_never_swallowed_by_the_extension()
    {
        // The extension walks only over characters the target's own tail accounts for. A heading
        // followed immediately by punctuation that is not part of it must not absorb it.
        var atoms = Atoms("Heading) leftover");
        var cursor = 0;

        var parts = StructuredSourcePartLocator.Locate(atoms, "Heading", punctuationInsensitive: true, ref cursor);

        Assert.Equal("Heading", Assert.Single(parts!).VerbatimText);
    }

    private static string Render(IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlyList<SemanticSourcePart> parts) =>
        SemanticSourceProjection.Render(
            SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts)).Parts);

    private static SemanticSourceAtom[] Atoms(params string[] texts) =>
        texts.Select((text, index) => new SemanticSourceAtom(
            $"L{index:0000}:S0", $"atom-{index}", index, 1, index, 0, text)).ToArray();
}
