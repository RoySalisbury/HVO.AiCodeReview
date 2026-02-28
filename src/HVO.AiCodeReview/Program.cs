using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration binding
// ---------------------------------------------------------------------------
builder.Services.Configure<AzureDevOpsSettings>(
    builder.Configuration.GetSection(AzureDevOpsSettings.SectionName));

builder.Services.Configure<AiProviderSettings>(
    builder.Configuration.GetSection(AiProviderSettings.SectionName));

// Keep legacy AzureOpenAI binding for backward-compatible fallback
builder.Services.Configure<AzureOpenAISettings>(
    builder.Configuration.GetSection(AzureOpenAISettings.SectionName));

// Assistants API settings for Vector Store review strategy
builder.Services.Configure<AssistantsSettings>(
    builder.Configuration.GetSection(AssistantsSettings.SectionName));

// ---------------------------------------------------------------------------
// HTTP client for Azure DevOps API calls (with Polly resilience)
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient<IDevOpsService, AzureDevOpsService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
})
.AddStandardResilienceHandler(options =>
{
    // ── Retry: exponential backoff with jitter for 408, 429, 5xx ────
    options.Retry.MaxRetryAttempts = 5;
    options.Retry.Delay = TimeSpan.FromSeconds(2);
    options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    // ShouldHandle already covers transient HTTP errors (408, 429, 5xx)
    // and HttpRequestException / TimeoutRejectedException by default.

    // ── Circuit Breaker: break after sustained failures ─────────────
    // SamplingDuration must be ≥ 2× AttemptTimeout for effective sampling.
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
    options.CircuitBreaker.FailureRatio = 0.9;
    options.CircuitBreaker.MinimumThroughput = 5;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

    // ── Total request timeout (outer) ──────────────────────────────
    options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);

    // ── Per-attempt timeout ────────────────────────────────────────
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
});

// ---------------------------------------------------------------------------
// AI code review service (single-provider or consensus — driven by config)
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<PromptAssemblyPipeline>(sp =>
    new PromptAssemblyPipeline(sp.GetRequiredService<ILoggerFactory>().CreateLogger<PromptAssemblyPipeline>()));
builder.Services.AddSingleton<ModelAdapterResolver>(sp =>
    new ModelAdapterResolver(sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelAdapterResolver>()));
builder.Services.AddCodeReviewService(builder.Configuration);
builder.Services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
builder.Services.AddSingleton<IGlobalRateLimitSignal, GlobalRateLimitSignal>();

// Vector Store review service (Assistants API — called directly by orchestrator)
builder.Services.AddScoped<VectorStoreReviewService>();

builder.Services.AddScoped<ICodeReviewOrchestrator, CodeReviewOrchestrator>();

// ---------------------------------------------------------------------------
// ASP.NET Core
// ---------------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "AI Code Review API",
        Version = "v1",
        Description = "Centralized AI-powered code review service for Azure DevOps pull requests.",
    });
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();
