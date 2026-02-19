using BuildingBlocks.Messaging.Extensions;
using BuildingBlocks.Observability.Extensions;
using BuildingBlocks.Persistence.Extensions;
using Notification.Worker.Notifications;

var builder = WebApplication.CreateBuilder(args);

builder.AddLabDefaults("Notification.Worker");
builder.Services.AddPostgresPersistence(builder.Configuration);
builder.Services.AddKafkaMessaging(builder.Configuration);
builder.Services.Configure<BuildingBlocks.Messaging.Resilience.ResilienceOptions>(builder.Configuration.GetSection(BuildingBlocks.Messaging.Resilience.ResilienceOptions.SectionName));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddHostedService<NotificationConsumerWorker>();

var app = builder.Build();
app.MapLabDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "Notification.Worker", status = "running" }));

app.Run();
