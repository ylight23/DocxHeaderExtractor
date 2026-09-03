namespace DocxHeaderExtractor.Application.Skills;

public enum SkillLifecycle
{
    Draft,
    Active,
    Deprecated,
    Retired,
}

/// <summary>
/// Provider-independent skill metadata. The skill body remains host-owned policy; this contract
/// carries only the versioned, machine-checkable requirements needed by a workflow runtime.
/// </summary>
public sealed record SkillDescriptor(
    string Name,
    string Version,
    string Digest,
    SkillLifecycle Lifecycle,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Guardrails,
    IReadOnlyList<string> Validators,
    bool HumanReviewBeforeMutation,
    int MaxRepairAttempts);

public sealed record SkillResolution(
    SkillDescriptor? Skill,
    string? FailureReason)
{
    public bool IsResolved => Skill is not null && FailureReason is null;

    public static SkillResolution Resolved(SkillDescriptor skill) => new(skill, null);
    public static SkillResolution Failed(string reason) => new(null, reason);
}

/// <summary>Exact skill catalog for framework adapters and host composition roots.</summary>
public sealed class SkillCatalog : ISkillCatalog
{
    private readonly IReadOnlyList<SkillDescriptor> _skills;

    public SkillCatalog(IEnumerable<SkillDescriptor> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        _skills = skills.Select(Validate).ToArray();
        var activeIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in _skills.Where(s => s.Lifecycle != SkillLifecycle.Retired))
        foreach (var identifier in new[] { skill.Name }.Concat(skill.Aliases))
            if (!activeIdentifiers.Add(identifier))
                throw new InvalidOperationException($"Skill identifier đã được đăng ký: {identifier}.");
    }

    public IReadOnlyList<SkillDescriptor> Skills => _skills;

    public SkillResolution Resolve(string identifier, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return SkillResolution.Failed("skill-identifier-missing");
        var matches = _skills
            .Where(skill => skill.Lifecycle is not SkillLifecycle.Draft and not SkillLifecycle.Retired)
            .Where(skill => version is null || string.Equals(skill.Version, version, StringComparison.Ordinal))
            .Where(skill => string.Equals(skill.Name, identifier, StringComparison.Ordinal) ||
                skill.Aliases.Contains(identifier, StringComparer.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => SkillResolution.Resolved(matches[0]),
            0 => SkillResolution.Failed("skill-not-found-or-inactive"),
            _ => SkillResolution.Failed("skill-ambiguous-version"),
        };
    }

    private static SkillDescriptor Validate(SkillDescriptor skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Version) ||
            string.IsNullOrWhiteSpace(skill.Digest))
            throw new ArgumentException("Skill name, version và digest không được rỗng.", nameof(skill));
        if (skill.MaxRepairAttempts is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(skill), "MaxRepairAttempts phải nằm trong 0..8.");
        return skill with { Aliases = skill.Aliases ?? [], Guardrails = skill.Guardrails ?? [], Validators = skill.Validators ?? [] };
    }
}

public interface ISkillCatalog
{
    IReadOnlyList<SkillDescriptor> Skills { get; }
    SkillResolution Resolve(string identifier, string? version = null);
}
