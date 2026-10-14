using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.7 (offline): the clarified task A-STRUCTURAL-OCCURRENCES. The user's instruction is carried verbatim as user content;
/// the system prompt stays V2.3; the plan labels the evaluation as development, not held-out, and keeps task A as the historical baseline.</summary>
public sealed class P7F1V6DirectD47Tests
{
    const string UserInstruction =
        "Trích xuất tất cả các heading thực sự tổ chức cấu trúc tài liệu, xét theo vai trò của từng lần xuất hiện tại chính vị trí đó.\n" +
        "Chọn những dòng hoặc nhóm dòng có chức năng đặt tên, mở đầu hoặc phân chia một phần nội dung của tài liệu.\n" +
        "Không chọn các dòng trong mục lục chỉ liệt kê hoặc dẫn chiếu đến heading xuất hiện ở vị trí khác. Tuy nhiên, vẫn xem xét các heading thực sự tổ chức chính mục lục, chỉ mục và những phần khác của tài liệu.\n" +
        "Sử dụng văn bản, bố cục và ngữ cảnh để xác định chức năng của từng lần xuất hiện. Khi chưa đủ bằng chứng, hãy yêu cầu thêm ngữ cảnh hoặc báo chưa thể quyết định thay vì suy đoán.";

    static readonly Lazy<JsonElement> Plan = new(() =>
    {
        using var doc = JsonDocument.Parse(P7F1V6DirectD45.Serialize(P7F1V6DirectD45.PinnedValues(TestRepository.Root(), "TEST_COMMIT")));
        return doc.RootElement.Clone();
    });

    [Fact]
    public void Instruction_IsTheUsersTextVerbatim()
    {
        Assert.Equal(UserInstruction, P7F1V6DirectD45.Instruction);
        Assert.Equal("A-STRUCTURAL-OCCURRENCES", P7F1V6DirectD45.TaskId);
    }

    [Fact]
    public void Instruction_IsUserContent_NotPartOfTheSystemPrompt()
    {
        Assert.DoesNotContain("mục lục", P7F1V6DirectV2.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("P7_F1_V6_DIRECT_PROMPT_V2_4", P7F1V6DirectV2.PromptVersion);
        Assert.Equal(P7F1V6DirectV2.SystemPromptSha256, P7F1V6DirectV2.ComputedSystemPromptSha256());

        var req = P7F1V6DirectV2.BuildRequest(P7F1V6DirectD45.Instruction, "sha", "SEGMENT", "SEG-000", 1, new[] { "L0000:S0" },
            new[] { new P7F1V6Direct.Line("L0000:S0", 1, "x") }, Array.Empty<P7F1V6DirectV2.Retrieval>());
        using var body = JsonDocument.Parse(req.Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(P7F1V6DirectV2.SystemPrompt, messages[0].GetProperty("content").GetString());
        using var payload = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(UserInstruction, payload.RootElement.GetProperty("instruction").GetString());
        Assert.Equal(P7F1V6Core.InstructionSha256(UserInstruction), payload.RootElement.GetProperty("instructionSha256").GetString());
    }

    [Fact]
    public void Plan_PinsTheClarifiedTask_AndLabelsItDevelopment()
    {
        var p = Plan.Value;
        Assert.Equal("p7.f1.v6.d4.8-plan.v1", p.GetProperty("artifact").GetString());
        Assert.Equal("A-STRUCTURAL-OCCURRENCES", p.GetProperty("task").GetString());
        Assert.Equal(UserInstruction, p.GetProperty("instruction").GetString());
        Assert.Equal(P7F1V6Core.InstructionSha256(UserInstruction), p.GetProperty("instructionSha256").GetString());
        Assert.Equal("Development evaluation — task clarification informed by previous model errors", p.GetProperty("evaluationLabel").GetString());
        Assert.Contains("Not held-out", p.GetProperty("notHeldOut").GetString(), StringComparison.Ordinal);
        var baseline = p.GetProperty("historicalBaseline");
        Assert.Equal("A", baseline.GetProperty("task").GetString());
        Assert.Equal("Trích xuất tất cả heading", baseline.GetProperty("instruction").GetString());
        Assert.Equal("340d7cbf5e8694ba411ec06aec6e6fe6a1db7082cf895a880c294f65e86978a6", baseline.GetProperty("consumedPlanSha256").GetString());
    }

    [Fact]
    public void Plan_KeepsEverythingElseAsDecided()
    {
        var p = Plan.Value;
        Assert.Equal("bcc381e5645df30f8ea0fbe39a0df9b9499f076344dcb656c65cb53342ea9a0e", p.GetProperty("systemPromptSha256").GetString());
        Assert.Equal("SRC-095", p.GetProperty("authorityId").GetString());
        Assert.Equal(48, p.GetProperty("unit").GetInt32());
        Assert.Equal(100, p.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(1.0, p.GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        Assert.Equal(131072, p.GetProperty("budget").GetProperty("maxRequestBytes").GetInt32());
        Assert.Equal(0, p.GetProperty("retries").GetInt32());
        Assert.Equal(3, p.GetProperty("earlyStop").GetProperty("consecutiveContractFailures").GetInt32());
        Assert.False(p.GetProperty("goldForScorerOnly").GetProperty("sentToProvider").GetBoolean());
        Assert.False(p.GetProperty("approvalToSend").GetBoolean());
    }
}
