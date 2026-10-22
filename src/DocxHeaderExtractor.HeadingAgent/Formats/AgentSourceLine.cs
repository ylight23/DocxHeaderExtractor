using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>One parser line as the agent sees it: alias, reading order, page, text, and the parser's bbox and typography (observations).</summary>
internal sealed record AgentSourceLine(string Alias, int Ordinal, int? Page, string Text, JsonElement? Bbox, JsonElement? Typography);
