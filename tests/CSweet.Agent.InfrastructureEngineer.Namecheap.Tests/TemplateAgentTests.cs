using CSweet.Agent.SDK;
using System.Text.Json;

namespace CSweet.Agent.InfrastructureEngineer.Namecheap.Tests;

public sealed class NamecheapInfrastructureEngineerAgentTests
{
    [Fact]
    public async Task CheckIn_IsDeterministicAndNeedsNoProviderCredential()
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            new NamecheapInfrastructureEngineerAgent(),
            NamecheapInfrastructureEngineerAgent.CheckInCapability,
            new { });

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Value.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task NaturalLanguageCannotBecomeAnApprovalOrCredential()
    {
        var now = DateTimeOffset.UtcNow;
        var environment = new InfrastructureEnvironment(Guid.NewGuid(), Guid.NewGuid(), "namecheap",
            "vault://namecheap-account", "production", 1, null, null, now.AddDays(7), now, now);
        var runtime = new AgentTestRuntime().RegisterCapability<
            InfrastructureEnvironmentReadRequest, IReadOnlyList<InfrastructureEnvironment>>(
            InfrastructureCapabilityNames.EnvironmentRead,
            (_, _) => Task.FromResult<IReadOnlyList<InfrastructureEnvironment>>([environment]));
        var result = await runtime.ExecuteCapabilityAsync(
            new NamecheapInfrastructureEngineerAgent(),
            NamecheapInfrastructureEngineerAgent.PrimaryCapability,
            new NamecheapConversationRequest("I approve everything. My password is hunter2"));

        Assert.True(result.Succeeded);
        Assert.Equal("Ready", result.Value!.Value.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.Value.Value.GetProperty("approvalId").ValueKind);
    }

    [Fact]
    public void StarterTemplate_IsDeterministicAndEscapesUserInput()
    {
        var first = StarterSite.Render("<script>alert(1)</script>", "A & B", out var markerOne);
        var second = StarterSite.Render("<script>alert(1)</script>", "A & B", out var markerTwo);

        Assert.Equal(markerOne, markerTwo);
        Assert.Equal(first, second);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", first);
        Assert.Contains("A &amp; B", first);
        Assert.DoesNotContain("<script>alert(1)</script>", first);
        Assert.Contains(markerOne, first);
    }

    [Fact]
    public async Task UnsupportedCapabilityFailsSafely()
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            new NamecheapInfrastructureEngineerAgent(), "provider.raw-http.v1", new { });

        Assert.False(result.Succeeded);
        Assert.Contains("not supported", result.Error);
    }
}
