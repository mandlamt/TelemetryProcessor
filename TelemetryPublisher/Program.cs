using Serilog;
using TelemetryPublisher.Data;
using TelemetryPublisher.Models;
using TelemetryPublisher.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// --- Configuration ---
builder.Services.Configure<PublisherOptions>(
    builder.Configuration.GetSection(PublisherOptions.SectionName));

// --- Services ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IPendingReadingRepository, SqlPendingReadingRepository>();

// The telemetry store holds in-process state (counters, queue), so it must be
// a singleton. It depends on a singleton repository which creates a new
// SqlConnection per call, so this is safe for concurrent access.
builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();

builder.Services.AddHostedService<ReadingGeneratorService>();

builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
