namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF sources the SRC Gold authorities are written against, and the Gold revision files
/// their review history names. Paths only - no protocol, engine or score lives here.
/// </summary>
internal static class SourcePdfCorpus
{
    internal const string Src029 = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf";
    internal const string Src041 = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf";
    internal const string Src042 = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/042_IDA_Financial_Statements_June_2025.pdf";
    internal const string Src044 = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/044_IDA_Financial_Statements_June_2024.pdf";
    internal const string Src053 = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/053_IDA_Information_Statement_FY25.pdf";
    internal const string Src054 = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/054_IBRD_Information_Statement_FY25.pdf";
    internal const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    internal const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    internal const string Src044Gold = "eval/a99-closed-loop/gold/SRC-044.gold.json";
    internal const string Src044GoldR1 = "eval/a99-closed-loop/gold-history/SRC-044.gold.r1.json";
    internal const string Src044Proposals = "eval/a99-closed-loop/generic-audit-v1_2/SRC-044.proposals.v1_2.json";

    internal const string Src054Gold = "eval/a99-closed-loop/gold/SRC-054.gold.json";
    internal const string Src054GoldR1 = "eval/a99-closed-loop/gold-history/SRC-054.gold.r1.json";
    internal const string Src054Proposals = "eval/a99-closed-loop/generic-audit-v1_2-pdf-facts-v2/SRC-054.proposals.json";
}
