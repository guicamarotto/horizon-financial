namespace BuildingBlocks.Messaging.Resilience;

public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public int MaxAttempts { get; set; } = 5;
    public int BaseDelayMs { get; set; } = 1000;
    public int MaxJitterMs { get; set; } = 300;
}
