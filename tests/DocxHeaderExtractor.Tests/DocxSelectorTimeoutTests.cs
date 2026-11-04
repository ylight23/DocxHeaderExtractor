using System.Reflection;
using System.Threading;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.Tests;

/// <summary>A DOCX selection call is not cut at the shared 300-second default: the PDF selector already works this way.</summary>
public sealed class DocxSelectorTimeoutTests
{
    [Fact]
    public void The_docx_selector_has_no_request_timeout_so_a_whole_document_call_can_finish()
    {
        var property = typeof(DocxSelector).GetProperty("RequestTimeout", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var value = (TimeSpan?)property.GetValue(new DocxSelector());

        Assert.Equal(Timeout.InfiniteTimeSpan, value);
    }
}
