using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Application.Capabilities;

namespace DocxHeaderExtractor.Application.Tasks;

/// <summary>Result of compiling one validated generic request into an executable task plan.</summary>
public sealed record CompiledTaskPlan(
    SemanticTaskPlan Semantic,
    ExecutionPlan Execution);

/// <summary>
/// Provider-independent plan compiler. It creates stable identifiers from task identity and
/// capability metadata; it does not select tools, call providers, or create authority output.
/// </summary>
public static class TaskPlanCompiler
{
    public static CompiledTaskPlan Compile(
        AgentTaskRequest request,
        ValidatedIntent intent,
        CapabilityDescriptor capability,
        string inputContract,
        string outputContract)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputContract);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputContract);

        var planId = CreatePlanId(request, intent, capability, inputContract, outputContract);
        var semantic = new SemanticTaskPlan(planId, 1, "generic-task", intent);
        var maxSteps = request.Budget?.MaxSteps ?? 1;
        if (maxSteps is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(request), "Task budget MaxSteps phải nằm trong khoảng 1..64.");

        var maxExternalCalls = capability.SendsDataExternally
            ? request.Budget?.MaxProviderCalls ?? 1
            : 0;
        if (maxExternalCalls is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Task budget MaxProviderCalls không được âm.");

        var retry = RetryPolicy.None;
        retry.Validate();

        var execution = new ExecutionPlan(
            planId,
            [new ExecutionStep(
                "execute-" + capability.Name,
                capability.Name,
                [],
                inputContract,
                outputContract)],
            maxSteps,
            maxExternalCalls)
        {
            MaxWallTime = request.Budget?.MaxWallTime,
            Retry = retry,
            ExternalTransferRequired = capability.SendsDataExternally,
        };

        return new CompiledTaskPlan(semantic, execution);
    }

    private static string CreatePlanId(
        AgentTaskRequest request,
        ValidatedIntent intent,
        CapabilityDescriptor capability,
        string inputContract,
        string outputContract)
    {
        var identity = request.IdempotencyKey
            ?? string.Join("|", request.Resources.Select(resource =>
                $"{resource.ResourceId}:{resource.Kind}:{resource.MediaType}:{resource.Locator}"));
        var material = string.Join("\n", request.UserPrompt, intent.Operation,
            intent.Granularity, intent.StructuralDepth?.ToString() ?? "", intent.OutputShape,
            capability.Name, inputContract, outputContract, identity);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
        return "plan-" + digest[..16];
    }
}
