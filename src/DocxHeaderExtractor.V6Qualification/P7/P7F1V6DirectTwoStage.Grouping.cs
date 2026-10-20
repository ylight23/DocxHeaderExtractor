namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>The grouping prompt of the two-stage plan. Kept in its own file: the selection prompts and the plan itself stay in the main file.</summary>
internal static partial class P7F1V6DirectTwoStage
{
    public static readonly string GroupPrompt = string.Join("\n",
        Intro,
        "In this step the lines selected by the earlier step are fixed. targets are selected lines you must place in headings; context shows other lines, each with selected true or false. Each line has alias, page, text and layout. Every alias appears once: in targets or in context, never in both.",
        "Group the targets into headings: a heading is one selected line, or several selected lines that together form one heading, in document order. A heading may include selected lines from context. Every target is in exactly one heading. Only lines with selected true may be parts. You cannot change what was selected: if you think a line was selected or left out wrongly, list it in reviewRequests with a reason, and still group the selected targets.",
        ReplyLine,
        "Status COMPLETE: every target is in exactly one heading.",
        "{\"protocolVersion\":\"" + GroupVersion + "\",\"status\":\"COMPLETE\",\"groups\":[{\"parts\":[\"<alias from targets>\",\"<selected alias from targets or context>\"]}],\"reviewRequests\":[{\"alias\":\"<alias from targets or context>\",\"reason\":\"<why>\"}]}",
        "Status INCOMPLETE: you cannot group some targets from the evidence you have; give the groups you can and say why.",
        "{\"protocolVersion\":\"" + GroupVersion + "\",\"status\":\"INCOMPLETE\",\"groups\":[],\"incompleteReason\":\"<why>\"}",
        NeedsLine, Needs(GroupVersion),
        "Rules: parts are 1 to 8 aliases in document order. A heading that needs more than 8 parts is reported with status INCOMPLETE, never truncated. Do not add levels, parents or other fields.");
}
