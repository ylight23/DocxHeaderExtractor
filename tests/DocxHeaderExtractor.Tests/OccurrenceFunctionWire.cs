using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The wire name of an occurrence function as the frozen P6T artifacts record it. The enum was moved into Core with
/// idiomatic member names; <c>ToString()</c> is the member name, not the protocol token the frozen artifacts carry.
/// </summary>
internal static class OccurrenceFunctionWire
{
    public static string Wire(this OccurrenceFunction function) => function switch
    {
        OccurrenceFunction.EstablishesStructure => "ESTABLISHES_STRUCTURE",
        OccurrenceFunction.RepresentsStructure => "REPRESENTS_STRUCTURE",
        OccurrenceFunction.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };
}
