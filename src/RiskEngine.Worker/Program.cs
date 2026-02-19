using BuildingBlocks.Messaging.Extensions;
using BuildingBlocks.Observability.Extensions;
using BuildingBlocks.Persistence.Extensions;
using RiskEngine.Worker.Risk;

var builder = WebApplication.CreateBuilder(args);

builder.AddLabDefaults("RiskEngine.Worker");
builder.Services.AddPostgresPersistence(builder.Configuration);
builder.Services.AddKafkaMessaging(builder.Configuration);
builder.Services.AddRabbitMessaging(builder.Configuration);
builder.Services.Configure<BuildingBlocks.Messaging.Resilience.ResilienceOptions>(builder.Configuration.GetSection(BuildingBlocks.Messaging.Resilience.ResilienceOptions.SectionName));
builder.Services.Configure<RiskRulesOptions>(builder.Configuration.GetSection(RiskRulesOptions.SectionName));
builder.Services.AddSingleton<RiskEvaluator>();
builder.Services.AddHostedService<RiskEngineConsumerWorker>();

var app = builder.Build();
app.MapLabDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "RiskEngine.Worker", status = "running" }));

app.Run();
