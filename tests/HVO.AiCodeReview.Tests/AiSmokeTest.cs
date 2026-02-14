using System.ClientModel;
using System.Text.Json;
using AiCodeReview.Models;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace AiCodeReview.Tests;

/// <summary>
/// Smoke test that verifies the Azure OpenAI endpoint is reachable and responds.
/// This does NOT perform a code review — it sends a trivial prompt and checks
/// that a response comes back without errors.
///
/// Filtered out by default via TestCategory. Run manually:
///   dotnet test --filter "TestCategory=Manual"
///
/// Requires AzureOpenAI config in appsettings.Test.json.
/// </summary>
[TestClass]
[TestCategory("Manual")]
public class AiSmokeTest
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task AzureOpenAI_RespondsToSimplePrompt()
    {
        // ── Load config ──
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json")
            .Build();

        var aiSettings = config.GetSection("AzureOpenAI").Get<AzureOpenAISettings>();
        Assert.IsNotNull(aiSettings, "AzureOpenAI section missing from appsettings.Test.json.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(aiSettings.Endpoint), "AzureOpenAI:Endpoint is required.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(aiSettings.ApiKey), "AzureOpenAI:ApiKey is required.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(aiSettings.DeploymentName), "AzureOpenAI:DeploymentName is required.");

        Console.WriteLine($"  Endpoint:   {aiSettings.Endpoint}");
        Console.WriteLine($"  Deployment: {aiSettings.DeploymentName}");

        // ── Create client ──
        var client = new AzureOpenAIClient(
            new Uri(aiSettings.Endpoint),
            new ApiKeyCredential(aiSettings.ApiKey));

        var chatClient = client.GetChatClient(aiSettings.DeploymentName);

        // ── Send a trivial prompt ──
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a helpful assistant. Respond briefly."),
            new UserChatMessage("Say 'hello' and nothing else."),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
            MaxOutputTokenCount = 50,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ClientResult<ChatCompletion> response;
        try
        {
            response = await chatClient.CompleteChatAsync(messages, options);
        }
        catch (ClientResultException cex)
        {
            Assert.Fail($"Azure OpenAI returned an error: HTTP {cex.Status} — {cex.Message}");
            return; // unreachable but keeps compiler happy
        }
        sw.Stop();

        // ── Validate response ──
        Assert.IsNotNull(response.Value, "Response should not be null.");
        Assert.IsTrue(response.Value.Content.Count > 0, "Response should have content.");

        var text = response.Value.Content[0].Text;
        Assert.IsFalse(string.IsNullOrWhiteSpace(text), "Response text should not be empty.");

        var usage = response.Value.Usage;
        Console.WriteLine();
        Console.WriteLine($"  ✓ AI responded in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"    Response:    \"{text.Trim()}\"");
        Console.WriteLine($"    Model:       {response.Value.Model}");
        Console.WriteLine($"    Prompt tkns: {usage?.InputTokenCount}");
        Console.WriteLine($"    Compl. tkns: {usage?.OutputTokenCount}");
        Console.WriteLine($"    Total tkns:  {usage?.TotalTokenCount}");
        Console.WriteLine($"    Finish:      {response.Value.FinishReason}");
        Console.WriteLine();
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task AzureOpenAI_RespondsToJsonMode()
    {
        // ── Load config ──
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json")
            .Build();

        var aiSettings = config.GetSection("AzureOpenAI").Get<AzureOpenAISettings>()!;

        var client = new AzureOpenAIClient(
            new Uri(aiSettings.Endpoint),
            new ApiKeyCredential(aiSettings.ApiKey));

        var chatClient = client.GetChatClient(aiSettings.DeploymentName);

        // ── Send a prompt requesting JSON output (mimics the code review format) ──
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are a test assistant. Respond with valid JSON only, matching this schema:
                { "status": "ok", "message": "<any short message>" }
                """),
            new UserChatMessage("Respond with your status."),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
            MaxOutputTokenCount = 100,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await chatClient.CompleteChatAsync(messages, options);
        sw.Stop();

        var text = response.Value.Content[0].Text;
        Assert.IsFalse(string.IsNullOrWhiteSpace(text), "Response text should not be empty.");

        // Verify it's valid JSON
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            Assert.Fail($"AI response is not valid JSON: {text}");
        }

        Assert.IsTrue(doc!.RootElement.TryGetProperty("status", out var status),
            "Response JSON should have a 'status' property.");

        Console.WriteLine();
        Console.WriteLine($"  ✓ AI JSON mode responded in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"    Status:  {status.GetString()}");
        Console.WriteLine($"    Message: {doc.RootElement.GetProperty("message").GetString()}");
        Console.WriteLine($"    Tokens:  {response.Value.Usage?.TotalTokenCount}");
        Console.WriteLine();

        doc.Dispose();
    }
}
