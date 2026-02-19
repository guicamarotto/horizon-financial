using BuildingBlocks.Messaging.Extensions;
using BuildingBlocks.Observability.Extensions;
using BuildingBlocks.Persistence.Extensions;
using LimitService.Worker.Limits;

var builder = WebApplication.CreateBuilder(args);

builder.AddLabDefaults("LimitService.Worker");
builder.Services.AddPostgresPersistence(builder.Configuration);
builder.Services.AddKafkaMessaging(builder.Configuration);
builder.Services.AddRabbitMessaging(builder.Configuration);
builder.Services.Configure<BuildingBlocks.Messaging.Resilience.ResilienceOptions>(builder.Configuration.GetSection(BuildingBlocks.Messaging.Resilience.ResilienceOptions.SectionName));
builder.Services.Configure<LimitRulesOptions>(builder.Configuration.GetSection(LimitRulesOptions.SectionName));
builder.Services.AddHostedService<LimitServiceConsumerWorker>();

var app = builder.Build();
app.MapLabDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "LimitService.Worker", status = "running" }));

app.Run();
