using BuildingBlocks.Observability.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace BuildingBlocks.Observability.Extensions;

public static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddLabDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((context, _, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithEnvironmentName()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .WriteTo.Console();
        });

        var otlpEndpoint = builder.Configuration["Otel:Endpoint"] ?? "http://jaeger:4317";

        builder.Services
            .AddHealthChecks();

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(LabTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddMeter(LabTelemetry.MeterName)
                .AddRuntimeInstrumentation()
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddPrometheusExporter());

        return builder;
    }

    public static WebApplication MapLabDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapPrometheusScrapingEndpoint("/metrics");
        return app;
    }
}
