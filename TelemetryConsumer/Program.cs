using System.Threading.Channels;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using TelemetryConsumer.Data;
using TelemetryConsumer.Models;
using TelemetryConsumer.Services;
using TelemetryShared.Models;

var builder = Host.CreateApplicationBuilder(args);

// --- Logging ---
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

// --- Configuration ---
builder.Services.Configure<ConsumerOptions>(
    builder.Configuration.GetSection(ConsumerOptions.SectionName));

var consumerOptions = builder.Configuration
    .GetSection(ConsumerOptions.SectionName)
    .Get<ConsumerOptions>() ?? new ConsumerOptions();

// --- Channel for fetch -> process handoff (separate threads) ---
// Bounded to provide back-pressure: if processing falls behind, fetching
// will await capacity rather than growing memory unbounded.
builder.Services.AddSingleton(Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(100)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = true
}));

// --- HTTP client with Polly retry + exponential backoff ---
// Handles the "Publisher offline" resilience requirement: retries transient
// failures (5xx, network errors) with exponential backoff before the
// exception surfaces to FetchWorker.
builder.Services.AddHttpClient<ITelemetryClient, TelemetryClient>(client =>
{
    client.BaseAddress = new Uri(consumerOptions.PublisherBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
})
.AddPolicyHandler(GetRetryPolicy(consumerOptions));

// --- Data access ---
builder.Services.AddSingleton<IAnalysisResultRepository, SqlAnalysisResultRepository>();

// --- Analysis ---
builder.Services.AddSingleton<IAnalysisService, MovingAverageAnalysisService>();

// --- Hosted workers ---
builder.Services.AddHostedService<FetchWorker>();
builder.Services.AddHostedService<ProcessingWorker>();

var host = builder.Build();
host.Run();

/// <summary>
/// Exponential backoff retry policy: retries transient HTTP failures
/// (network errors, 5xx, request timeouts) with delays of
/// base, base*2, base*4, ... up to RetryMaxAttempts, logging each attempt.
/// </summary>
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(ConsumerOptions options)
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(response => response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        .WaitAndRetryAsync(
            retryCount: options.RetryMaxAttempts,
            sleepDurationProvider: attempt =>
                TimeSpan.FromSeconds(options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1)),
            onRetry: (outcome, timespan, attempt, _) =>
            {
                Log.Warning(
                    "Retry {Attempt}/{Max} calling Publisher after {Delay}s. Reason: {Reason}",
                    attempt, options.RetryMaxAttempts, timespan.TotalSeconds,
                    outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
            });
}
