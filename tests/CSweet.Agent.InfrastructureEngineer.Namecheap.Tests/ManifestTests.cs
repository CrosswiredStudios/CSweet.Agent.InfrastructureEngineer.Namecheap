using CSweet.Agent.SDK;

namespace CSweet.Agent.InfrastructureEngineer.Namecheap.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task ManifestDeclaresExactNamecheapSurfaceAndConfinedSftp()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "csweet-plugin.json"));
        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);

        Assert.Equal("com.csweet.infrastructure-engineer.namecheap", manifest.Id);
        Assert.Contains("infrastructure-engineer", manifest.RolePolicy!.DeclaredRoleKeys);
        Assert.Contains("namecheap", manifest.RolePolicy.SpecializationKeys);
        var server = Assert.Single(manifest.McpServers);
        Assert.Equal("https://mcp.namecheap.com/mcp", server.Endpoint);
        Assert.Equal(10, server.Tools.Count);
        var transfer = Assert.Single(manifest.FileTransferTargets);
        Assert.Equal(new[] { "probe", "list", "stat", "upload" }, transfer.Operations);
        Assert.Equal(21098, transfer.Port);
        Assert.Equal("public_html", transfer.RootPath);
        Assert.DoesNotContain(manifest.ProviderOperations,
            x => x.Command.Contains("shell", StringComparison.OrdinalIgnoreCase));
    }
}
