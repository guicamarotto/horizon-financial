using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BuildingBlocks.Observability.Telemetry;

public static class LabTelemetry
{
    public const string MeterName = "EventDrivenFinanceLab";
    public const string ActivitySourceName = "EventDrivenFinanceLab.Activities";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>("processed_count");
    public static readonly Counter<long> FailedCounter = Meter.CreateCounter<long>("failed_count");
    public static readonly Counter<long> DlqCounter = Meter.CreateCounter<long>("dlq_count");
}
