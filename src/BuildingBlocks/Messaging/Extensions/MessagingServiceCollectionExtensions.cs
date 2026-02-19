using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Options;
using BuildingBlocks.Messaging.Rabbit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Messaging.Extensions;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddKafkaMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.AddSingleton<IKafkaPublisher, KafkaPublisher>();
        return services;
    }

    public static IServiceCollection AddRabbitMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitOptions>(configuration.GetSection(RabbitOptions.SectionName));
        services.AddSingleton<IRabbitConnectionProvider, RabbitConnectionProvider>();
        services.AddSingleton<IRabbitPublisher, RabbitPublisher>();
        return services;
    }
}
