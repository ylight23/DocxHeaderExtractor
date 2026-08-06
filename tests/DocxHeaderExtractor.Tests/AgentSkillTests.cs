using DocxHeaderExtractor.AgentHarness;

namespace DocxHeaderExtractor.Tests;

public sealed class AgentSkillTests
{
    /// <summary>
    /// Chuẩn hoá xuống dòng về LF. Các test dưới cắt bỏ nguyên một DÒNG bằng Replace kèm ký tự
    /// xuống dòng; raw string literal lại mang đúng kiểu xuống dòng của FILE NGUỒN, mà git tự đổi
    /// LF↔CRLF theo cấu hình máy. Không chuẩn hoá thì test xanh trên cây checkout LF và đỏ trên
    /// cây CRLF — đúng như đã xảy ra sau một lượt checkout lại.
    /// </summary>
    private static string Minimal => MinimalRaw.ReplaceLineEndings("\n");

    private const string MinimalRaw = """
        ---
        name: heading-extraction
        description: mô tả
        version: 1.1.0
        requires:
          guardrails: [input_document, external_data_transfer]
          validators: [outline_grounding]
          humanReviewBeforeWriteback: true
          maxRepairAttempts: 1
        ---

        # Tiêu đề

        ## Mục tiêu

        Nội dung.

        ## Điều kiện dừng

        Nội dung.
        """;

    [Fact]
    public void Front_matter_requirements_become_the_runtime_contract()
    {
        var skill = AgentSkillLoader.Parse(Minimal, "(test)", "abc123");

        Assert.Equal("heading-extraction", skill.Name);
        Assert.Equal("1.1.0", skill.Version);
        Assert.Equal(["input_document", "external_data_transfer"], skill.Requires.Guardrails);
        Assert.Equal(["outline_grounding"], skill.Requires.Validators);
        Assert.True(skill.Requires.HumanReviewBeforeWriteback);
        Assert.Equal(1, skill.Requires.MaxRepairAttempts);
        Assert.Equal(["Mục tiêu", "Điều kiện dừng"], skill.Sections);
    }

    [Fact]
    public void Missing_version_is_rejected_rather_than_defaulted()
    {
        var text = Minimal.Replace("version: 1.1.0\n", "");

        var error = Assert.Throws<AgentSkillException>(
            () => AgentSkillLoader.Parse(text, "(test)", "abc123"));

        Assert.Contains("version", error.Message);
    }

    [Fact]
    public void Unknown_requirement_keys_fail_closed()
    {
        var text = Minimal.Replace("  maxRepairAttempts: 1", "  allowAnything: true");

        var error = Assert.Throws<AgentSkillException>(
            () => AgentSkillLoader.Parse(text, "(test)", "abc123"));

        Assert.Contains("allowAnything", error.Message);
    }

    [Fact]
    public void Text_without_front_matter_is_not_a_skill()
    {
        var error = Assert.Throws<AgentSkillException>(
            () => AgentSkillLoader.Parse("# Chỉ là markdown", "(test)", "abc123"));

        Assert.Contains("front matter", error.Message);
    }

    [Fact]
    public void Repair_ceiling_outside_the_supported_range_is_rejected()
    {
        var text = Minimal.Replace("maxRepairAttempts: 1", "maxRepairAttempts: 99");

        Assert.Throws<AgentSkillException>(() => AgentSkillLoader.Parse(text, "(test)", "abc123"));
    }

    [Fact]
    public void Shipped_skill_file_travels_with_the_build_output()
    {
        var skill = AgentSkillLoader.LoadDefault();

        Assert.Equal("heading-extraction", skill.Name);
        Assert.Contains("outline_grounding", skill.Requires.Validators);
        Assert.True(skill.Requires.HumanReviewBeforeWriteback);
        Assert.Equal(12, skill.Digest.Length);
        Assert.Contains("Điều kiện dừng", skill.Sections);
    }
}
