using AiCodeReview.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="CorrelationMiddleware"/> — validates that
/// X-Correlation-ID headers are propagated, generated, and validated.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class CorrelationMiddlewareTests
{
    private const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>
    /// Build a test host with CorrelationMiddleware and a terminal handler
    /// that echoes the CorrelationContext.Current value.
    /// </summary>
    private static async Task<IHost> CreateTestHost()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                });
                webBuilder.Configure(app =>
                {
                    app.UseCorrelation();
                    app.Run(async context =>
                    {
                        // Echo back the correlation ID that the middleware set
                        var corrId = HVO.Enterprise.Telemetry.Correlation.CorrelationContext.Current;
                        await context.Response.WriteAsync($"CorrelationId={corrId}");
                    });
                });
            })
            .StartAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Basic behavior
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task NoHeader_GeneratesCorrelationId_AndReturnsInResponse()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/test");

        response.EnsureSuccessStatusCode();
        Assert.IsTrue(response.Headers.Contains(CorrelationHeader),
            "Response should contain X-Correlation-ID header");

        var responseId = response.Headers.GetValues(CorrelationHeader).First();
        Assert.IsTrue(Guid.TryParse(responseId, out _),
            $"Generated correlation ID should be a valid GUID, got: {responseId}");
    }

    [TestMethod]
    public async Task ValidHeader_PreservesCorrelationId()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();
        var expectedId = Guid.NewGuid().ToString("D");

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeader, expectedId);

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var responseId = response.Headers.GetValues(CorrelationHeader).First();
        Assert.AreEqual(expectedId, responseId,
            "Should preserve the caller-supplied correlation ID");
    }

    [TestMethod]
    public async Task ValidHeader_SetsCorrelationContext()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();
        var expectedId = "my-custom-correlation-id";

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeader, expectedId);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.IsTrue(body.Contains(expectedId),
            $"CorrelationContext.Current should be the provided ID. Body: {body}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Validation — invalid IDs get replaced
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task EmptyHeader_GeneratesNew()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation(CorrelationHeader, "");

        var response = await client.SendAsync(request);
        var responseId = response.Headers.GetValues(CorrelationHeader).First();

        Assert.IsTrue(Guid.TryParse(responseId, out _),
            "Empty header should be replaced with a generated GUID");
    }

    [TestMethod]
    public async Task WhitespaceHeader_GeneratesNew()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation(CorrelationHeader, "   ");

        var response = await client.SendAsync(request);
        var responseId = response.Headers.GetValues(CorrelationHeader).First();

        Assert.IsTrue(Guid.TryParse(responseId, out _),
            "Whitespace-only header should be replaced with a generated GUID");
    }

    [TestMethod]
    public async Task InvalidCharsInHeader_GeneratesNew()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation(CorrelationHeader, "has spaces & <special> chars!");

        var response = await client.SendAsync(request);
        var responseId = response.Headers.GetValues(CorrelationHeader).First();

        Assert.IsTrue(Guid.TryParse(responseId, out _),
            "Header with invalid chars should be replaced with a generated GUID");
    }

    [TestMethod]
    public async Task TooLongHeader_GeneratesNew()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var longId = new string('a', 200);
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.TryAddWithoutValidation(CorrelationHeader, longId);

        var response = await client.SendAsync(request);
        var responseId = response.Headers.GetValues(CorrelationHeader).First();

        Assert.IsTrue(Guid.TryParse(responseId, out _),
            "Header exceeding 128 chars should be replaced with a generated GUID");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Allowed special chars
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow("abc-123", DisplayName = "Hyphens")]
    [DataRow("abc_123", DisplayName = "Underscores")]
    [DataRow("abc.123", DisplayName = "Dots")]
    [DataRow("ABC123", DisplayName = "Uppercase")]
    public async Task ValidSpecialChars_Preserved(string id)
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add(CorrelationHeader, id);

        var response = await client.SendAsync(request);
        var responseId = response.Headers.GetValues(CorrelationHeader).First();

        Assert.AreEqual(id, responseId,
            $"Valid correlation ID '{id}' should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Extension method
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void UseCorrelation_ExtensionMethod_ReturnsAppBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = new ApplicationBuilder(services.BuildServiceProvider());

        var result = builder.UseCorrelation();

        Assert.IsNotNull(result, "UseCorrelation() should return the IApplicationBuilder");
    }
}
