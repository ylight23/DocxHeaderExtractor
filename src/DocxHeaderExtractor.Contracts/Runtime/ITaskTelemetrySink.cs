namespace DocxHeaderExtractor.Contracts.Runtime;

public interface ITaskTelemetrySink
{
    ValueTask RecordAsync(TaskTelemetryEvent telemetry, CancellationToken ct = default);
}
