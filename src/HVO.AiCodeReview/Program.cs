using AiCodeReview.Models;
using AiCodeReview.Services;

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

// ---------------------------------------------------------------------------
// HTTP client for Azure DevOps API calls
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

// ---------------------------------------------------------------------------
// AI code review service (single-provider or consensus — driven by config)
// ---------------------------------------------------------------------------
builder.Services.AddCodeReviewService(builder.Configuration);
builder.Services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
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
