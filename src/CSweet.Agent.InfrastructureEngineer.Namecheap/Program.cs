using CSweet.Agent.SDK;
using CSweet.Agent.InfrastructureEngineer.Namecheap;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
        new NamecheapInfrastructureEngineerAgent(), NamecheapInfrastructureEngineerAgent.CheckInCapability,
        new { });
    Console.WriteLine(result.Value);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<NamecheapInfrastructureEngineerAgent>();
await builder.Build().RunAsync();
