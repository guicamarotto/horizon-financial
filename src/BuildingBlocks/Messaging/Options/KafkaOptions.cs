namespace BuildingBlocks.Messaging.Options;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "kafka:29092";
    public string? GroupId { get; set; }
}
