using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.InfrastructureEngineer.Namecheap;

public sealed record NamecheapConversationRequest(string? Input);
public sealed record NamecheapConversationResponse(string Message, string Status, Guid? ApprovalId = null,
    JsonElement? Details = null);

public sealed partial class NamecheapInfrastructureEngineerAgent : CSweetAgentBase
{
    public const string PrimaryCapability = "assistant.converse.v1";
    public const string CheckInCapability = "management.check-in.v1";
    public const string DomainsList = "namecheap.mcp.domains-list.v1";
    public const string DomainsCheck = "namecheap.mcp.domains-check-availability.v1";
    public const string DomainPurchaseLink = "namecheap.mcp.domain-purchase-link.v1";
    public const string DnsGet = "namecheap.mcp.dns-records-get.v1";
    public const string DnsSave = "namecheap.mcp.dns-records-save.v1";
    public const string HostingCheckout = "namecheap.hosting.checkout-handoff.v1";
    public const string PublicSiteVerify = "namecheap.public-site.verify.v1";

    public override string AgentId => "com.csweet.infrastructure-engineer.namecheap";
    public override string Version => "0.1.0";

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) => builder
        .LlmProvider("llmProvider", "Language model provider", true,
            "A small tool-capable model is sufficient because all provider operations are typed.")
        .LlmModel("llmModel", "Language model", "llmProvider", true)
        .Text("businessDisplayName", "Business display name", true)
        .TextArea("preferredDomains", "Preferred domains", true)
        .Select("hostingNeeds", "Hosting needs", [new("starter", "Coming Soon page"),
            new("small-site", "Small business website"), new("multi-site", "Multiple websites")], true,
            defaultValue: "starter")
        .Select("expectedTraffic", "Expected traffic", [new("low", "Low"), new("medium", "Medium"),
            new("high", "High")], true, defaultValue: "low")
        .Boolean("emailNeeded", "Business email needed", true)
        .Number("maximumInitialBudget", "Maximum initial budget (USD)", true,
            minimum: 1, maximum: 100000, step: 1, defaultValue: 100)
        .Boolean("autoRenew", "Enable auto-renew", true, defaultValue: true)
        .TextArea("starterMessage", "Coming Soon message", false,
            defaultValue: "We are building something useful. Please check back soon.")
        .Select("hostingPlan", "Shared Hosting plan", [new("Stellar", "Stellar"),
            new("Stellar Plus", "Stellar Plus"), new("Stellar Business", "Stellar Business")], true,
            defaultValue: "Stellar")
        .Text("hostingHost", "Namecheap Shared Hosting hostname", false)
        .Text("hostingIPv4", "Namecheap Shared Hosting IPv4", false)
        .Select("approvalMode", "Approval route", [new("CEO Approval", "CEO approval")], true,
            defaultValue: "CEO Approval")
        .Secret("namecheap-contact", "Registration contact JSON", false)
        .Secret("namecheap-api", "Legacy Namecheap API JSON", false)
        .Secret("namecheap-sftp", "Namecheap SFTP JSON", false);

    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context,
        CancellationToken token)
    {
        if (message.EventType != AgentLifecycleEvents.Onboarded) return;
        await EnvironmentAsync(context, token);
        await context.ReportProgressAsync(new { stage = "ready", message = "Namecheap infrastructure workspace initialized." }, token);
        await context.Platform.Lifecycle.CompleteOnboardingAsync(message, token);
    }

    public override async Task<PersonalTodoResult> HandlePersonalTodoAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (!item.Title.Contains("Namecheap", StringComparison.OrdinalIgnoreCase))
            return PersonalTodoResult.Blocked("Only Namecheap infrastructure monitoring is supported.");
        try
        {
            var report = await context.Platform.Infrastructure.ReconcileAsync(
                new((await EnvironmentAsync(context, token)).Id), token);
            return report.Status == "Reconciled" ? PersonalTodoResult.Completed("Weekly reconciliation completed.")
                : PersonalTodoResult.Blocked(string.Join(" ", report.RequiredActions));
        }
        catch (PlatformCapabilityException e) when (e.Message.Contains("not due", StringComparison.OrdinalIgnoreCase))
        { return PersonalTodoResult.Completed("No weekly reconciliation is due."); }
    }

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(AgentCapabilityRequest request,
        AgentRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Capability == CheckInCapability)
            return AgentWorkResult.Success(new { healthy = true, checkedAt = DateTimeOffset.UtcNow });
        if (request.Capability != PrimaryCapability)
            return AgentWorkResult.Failure($"Capability '{request.Capability}' is not supported.");
        NamecheapConversationRequest? input;
        try { input = DeserializePayload<NamecheapConversationRequest>(request.Arguments); }
        catch (JsonException) { return AgentWorkResult.Failure("The request payload is not valid."); }
        if (string.IsNullOrWhiteSpace(input?.Input)) return AgentWorkResult.Failure("input is required.");
        try
        {
            await context.ReportProgressAsync(new { stage = "understanding-request" }, token);
            return AgentWorkResult.Success(await ConverseAsync(input.Input.Trim(), context, token));
        }
        catch (Exception e) when (e is PlatformCapabilityException or InvalidOperationException)
        { return AgentWorkResult.Failure(e.Message); }
    }

    private async Task<NamecheapConversationResponse> ConverseAsync(string input, AgentRuntimeContext context,
        CancellationToken token)
    {
        var lower = input.ToLowerInvariant();
        var environment = await EnvironmentAsync(context, token);
        var domain = ExtractDomain(input) ?? ConfiguredDomains().FirstOrDefault();

        if (lower.Contains("status") || lower.Contains("progress"))
        {
            var changes = await context.Platform.Infrastructure.ReadChangesAsync(new(EnvironmentId: environment.Id), token);
            var latest = changes.FirstOrDefault();
            if (latest?.Status == "Approved")
            {
                var receipts = await context.Platform.Infrastructure.ExecuteApprovedChangeAsync(
                    new(latest.Id, latest.PayloadHash), token);
                var action = receipts.Select(x => FindCheckoutAction(x.SanitizedResult)).FirstOrDefault(x => x is not null);
                var message = action is null
                    ? $"The latest change, '{latest.Summary}', is approved and has a verified operation receipt."
                    : $"The latest change is approved. Continue with the platform-owned checkout action: {action}";
                return new(message, action is null ? "Approved" : "CheckoutRequired", latest.Id,
                    JsonSerializer.SerializeToElement(receipts));
            }
            return new(latest is null ? "No provider changes have been proposed yet." :
                $"The latest change, '{latest.Summary}', is {latest.Status}.", "Status",
                latest?.Status == "Pending" ? latest.Id : null,
                latest is null ? null : JsonSerializer.SerializeToElement(latest));
        }

        if (lower.Contains("completed checkout") || lower.Contains("paid for") || lower.Contains("finished checkout"))
        {
            if (domain is null) return Ask("Which domain should I verify with Namecheap?");
            var result = await ReadAsync(context, environment.Id, DomainsList,
                JsonSerializer.SerializeToElement(new { domain }), $"verify-domain:{domain}", token);
            var owned = result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array &&
                items.EnumerateArray().Any(x => x.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), domain, StringComparison.OrdinalIgnoreCase) &&
                    x.TryGetProperty("lifecycleStatus", out var state) && state.GetString() == "registered");
            return owned ? new($"Namecheap confirms {domain} is registered in the connected account.", "DomainOwned", Details: result)
                : new($"Namecheap does not yet report {domain} as registered. I will not mark it purchased.",
                    "AwaitingProviderConfirmation", Details: result);
        }

        if ((lower.Contains("buy") || lower.Contains("purchase")) && lower.Contains("host"))
        {
            if (domain is null) return Ask("Which owned domain should Shared Hosting serve?");
            var plan = Settings.GetString("hostingPlan", RecommendPlan());
            var maximum = Settings.GetDecimal("maximumInitialBudget", 100);
            var operation = new InfrastructureOperation(HostingCheckout,
                JsonSerializer.SerializeToElement(new { domain, plan }), "fiscal-write", $"hosting-checkout:{domain}:{plan}");
            var change = await ProposeAsync(context, environment.Id,
                $"Purchase Namecheap {plan} Shared Hosting for {domain}; maximum initial charge USD {maximum:0.00}; checkout renewal terms are authoritative.",
                [operation], new(true, 0, maximum, "USD", true, "Namecheap checkout terms", "WithinBudget"), token);
            return Await(change, "Approve the exact hosting plan, maximum initial charge, renewal commitment, and domain. Payment, MFA, and anti-bot checks stay on Namecheap.");
        }

        if ((lower.Contains("buy") || lower.Contains("purchase") || lower.Contains("register")) && domain is not null)
        {
            var result = await ReadAsync(context, environment.Id, DomainsCheck,
                JsonSerializer.SerializeToElement(new { domains = new[] { domain } }), $"availability-before-purchase:{domain}", token);
            var quote = Quote(result, domain);
            if (quote.Result != "available")
                return new($"Namecheap reports {domain} as {quote.Result}; no purchase was proposed.", "Unavailable", Details: result);
            if (quote.Amount is null)
                return new($"{domain} is available but has no reliable price. Set an explicit maximum for a CEO exception.", "PriceUnknown", Details: result);
            var budget = Settings.GetDecimal("maximumInitialBudget", 100);
            if (quote.Amount > budget)
                return new($"The {quote.Currency} {quote.Amount:0.00} quote exceeds the configured USD {budget:0.00} maximum.", "OverBudget", Details: result);
            var renew = Settings.GetBoolean("autoRenew", true);
            var operation = new InfrastructureOperation(DomainPurchaseLink,
                JsonSerializer.SerializeToElement(new { domain, autoRenew = renew }), "fiscal-write", $"domain-consent:{domain}:{renew}");
            var years = quote.PricedYears ?? 1;
            var change = await ProposeAsync(context, environment.Id,
                $"Register {domain} for {years} year(s), maximum {quote.Currency} {quote.Amount:0.00}, privacy high/included where eligible, auto-renew explicitly {(renew ? "enabled" : "disabled")}.",
                [operation], new(true, quote.Amount, quote.Amount, quote.Currency, renew, "registration term", "WithinBudget"), token);
            return Await(change, "Approval creates a hosted Namecheap consent link only; it does not charge or register the domain.");
        }

        if (lower.Contains("search") || lower.Contains("available") || lower.Contains("check domain"))
        {
            var domains = domain is null ? ConfiguredDomains() : [domain];
            if (domains.Length == 0) return Ask("Tell me one or more domain names to check.");
            var result = await ReadAsync(context, environment.Id, DomainsCheck,
                JsonSerializer.SerializeToElement(new { domains = domains.Take(20).ToArray() }),
                $"availability:{Hash(string.Join(',', domains))}", token);
            return new("I checked current Namecheap availability and registration pricing. Choose an available domain and tell me to purchase it.",
                "DomainOptions", Details: result);
        }

        if (lower.Contains("activate hosting") || lower.Contains("verify hosting"))
        {
            var host = Settings.GetString("hostingHost");
            if (string.IsNullOrWhiteSpace(host))
                return new("Use the secure settings form for the hosting hostname and vaulted SFTP credential. Never paste a password into chat.", "SecureActivationRequired");
            var result = await context.Platform.Infrastructure.TransferFileAsync(new("namecheap-shared-hosting",
                "probe", host, "", null, null, null, $"sftp-probe:{host}"), token);
            return new("The hosting account accepted the restricted, host-key-pinned SFTP probe on port 21098.",
                "HostingActivated", Details: JsonSerializer.SerializeToElement(result));
        }

        if (lower.Contains("publish") && (lower.Contains("coming soon") || lower.Contains("starter")))
        {
            if (domain is null) return Ask("Which domain should receive the Coming Soon page?");
            var host = Settings.GetString("hostingHost");
            if (string.IsNullOrWhiteSpace(host)) return new("Complete secure hosting activation first.", "SecureActivationRequired");
            var listing = await context.Platform.Infrastructure.TransferFileAsync(new("namecheap-shared-hosting",
                "list", host, "", null, null, null, $"sftp-list:{host}"), token);
            if (listing.Length is > 0)
                return new("public_html already contains content. I stopped before writing and need an explicit overwrite or alternate-path decision.",
                    "ExistingContentDecisionRequired", Details: JsonSerializer.SerializeToElement(listing));
            var page = StarterSite.Render(Settings.GetString("businessDisplayName", domain),
                Settings.GetString("starterMessage"), out var marker);
            var bytes = Encoding.UTF8.GetBytes(page);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var transfer = new InfrastructureFileTransferRequest("namecheap-shared-hosting", "upload", host,
                "index.html", bytes, digest, null, $"starter-upload:{domain}:{digest}");
            var operation = new InfrastructureOperation(InfrastructureCapabilityNames.FileTransfer,
                JsonSerializer.SerializeToElement(transfer), "security-sensitive-write", transfer.IdempotencyKey);
            var change = await ProposeAsync(context, environment.Id,
                $"Upload public_html/index.html for {domain}; SHA-256 {digest}; marker {marker}.",
                [operation], NoFiscal(), token);
            return Await(change, "public_html is empty. Approve the exact file, path, bytes, and digest before upload.");
        }

        if (lower.Contains("connect domain") || lower.Contains("configure dns") || lower.Contains("point domain"))
        {
            if (domain is null) return Ask("Which domain should I connect to Shared Hosting?");
            var ipv4 = Settings.GetString("hostingIPv4");
            if (!IPAddress.TryParse(ipv4, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return new("Enter the Shared Hosting IPv4 through the platform settings form.", "HostingAddressRequired");
            var current = await ReadAsync(context, environment.Id, DnsGet,
                JsonSerializer.SerializeToElement(new { domainName = domain, take = 500 }), $"dns-before:{domain}", token);
            if (HasOmittedRecords(current))
                return new("Namecheap reports records its typed MCP contract cannot represent. Automated DNS writing is blocked to prevent loss.",
                    "UnrepresentableDnsRecords", Details: current);
            if (!current.TryGetProperty("records", out var currentRecords) || currentRecords.ValueKind != JsonValueKind.Array)
                return new("Namecheap did not return a complete representable DNS record set. Automated DNS writing is blocked.",
                    "IncompleteDnsState", Details: current);
            var records = currentRecords.EnumerateArray().Where(record => !IsManagedWebRecord(record))
                .Select(record => record.Clone()).ToList();
            records.Add(JsonSerializer.SerializeToElement(new { type = "A", name = "@", address = ipv4, ttl = 300 }));
            records.Add(JsonSerializer.SerializeToElement(new { type = "CNAME", name = "www", cname = domain, ttl = 300 }));
            var operation = new InfrastructureOperation(DnsSave,
                JsonSerializer.SerializeToElement(new { domainName = domain, records }), "write", $"dns-connect:{domain}:{ipv4}");
            var change = await ProposeAsync(context, environment.Id,
                $"Preserve unrelated DNS and add/update Shared Hosting records for {domain}.", [operation], NoFiscal(), token);
            return Await(change, "The current zone is representable. The DNS write is hash-bound and never exposes Namecheap force.");
        }

        if (lower.Contains("https") || lower.Contains("ssl") || lower.Contains("verify site") || lower.Contains("site live"))
        {
            if (domain is null) return Ask("Which domain should I verify over HTTPS?");
            var marker = StarterSite.Marker(Settings.GetString("businessDisplayName", domain), Settings.GetString("starterMessage"));
            var verified = await ReadAsync(context, environment.Id, PublicSiteVerify,
                JsonSerializer.SerializeToElement(new { domain, expectedMarker = marker }), $"https-verify:{domain}", token);
            var ready = verified.TryGetProperty("httpsValid", out var https) && https.GetBoolean() &&
                verified.TryGetProperty("markerMatched", out var matched) && matched.GetBoolean();
            if (!ready) return new("The public HTTPS smoke test is not ready. DNS or the included certificate may still be propagating.", "HttpsPending", Details: verified);
            var contract = await context.Platform.Infrastructure.PublishDeploymentContractAsync(new(environment.Id,
                domain, Settings.GetString("hostingHost"), [$"https://{domain}/"],
                ["A apex resolves to Shared Hosting", $"CNAME www resolves to {domain}"],
                ["Future releases require an approved deployment agent", $"Starter marker: {marker}"],
                ["vault://namecheap-sftp"], $"deployment-contract:{domain}:{marker}"), token);
            return new($"{domain} resolves, serves the approved marker, and has a valid certificate. Hosting is ready; deployment contract v{contract.Version} is published.",
                "HostingReady", Details: JsonSerializer.SerializeToElement(contract));
        }

        return new("I can connect Namecheap, search and purchase a domain through native approval, guide Shared Hosting checkout, securely activate SFTP, preserve DNS, publish the bundled Coming Soon page, verify HTTPS, and monitor it weekly. Tell me a domain to search.", "Ready");
    }

    private static async Task<JsonElement> ReadAsync(AgentRuntimeContext context, Guid environmentId,
        string capability, JsonElement input, string key, CancellationToken token)
    {
        var change = await context.Platform.Infrastructure.ProposeChangeAsync(new(environmentId, null, null,
            $"Read Namecheap provider state through {capability}.",
            [new InfrastructureOperation(capability, input, "read", key)], NoFiscal(), "No provider state is changed.",
            DateTimeOffset.UtcNow.AddMinutes(10), key), token);
        var receipts = await context.Platform.Infrastructure.ExecuteApprovedChangeAsync(new(change.Id, change.PayloadHash), token);
        return receipts.Single().SanitizedResult;
    }

    private static Task<InfrastructureChangeSet> ProposeAsync(AgentRuntimeContext context, Guid environmentId,
        string summary, IReadOnlyList<InfrastructureOperation> operations, InfrastructureFiscalImpact fiscal,
        CancellationToken token) => context.Platform.Infrastructure.ProposeChangeAsync(new(environmentId, null, null,
            summary, operations, fiscal, "Reconcile provider state and reverse only the exact changed records/files when supported.",
            DateTimeOffset.UtcNow.AddMinutes(fiscal.HasFiscalImpact ? 20 : 60), $"proposal:{Hash(summary)}"), token);

    private static NamecheapConversationResponse Await(InfrastructureChangeSet change, string message) =>
        new(message, "AwaitingApproval", change.Id, JsonSerializer.SerializeToElement(change));
    private static NamecheapConversationResponse Ask(string message) => new(message, "DomainRequired");
    private static InfrastructureFiscalImpact NoFiscal() => new(false, null, null, "USD", false, null, "NotApplicable");
    private static Task<IReadOnlyList<InfrastructureEnvironment>> EnvironmentsAsync(AgentRuntimeContext context,
        CancellationToken token) => context.Platform.Infrastructure.ReadEnvironmentsAsync(new(Provider: "namecheap"), token);
    private static async Task<InfrastructureEnvironment> EnvironmentAsync(AgentRuntimeContext context, CancellationToken token) =>
        (await EnvironmentsAsync(context, token)).Single();

    private string[] ConfiguredDomains() => Settings.GetString("preferredDomains")
        .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(ExtractDomain).Where(x => x is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private string RecommendPlan() => Settings.GetString("expectedTraffic") == "high" ||
        Settings.GetString("hostingNeeds") == "multi-site" ? "Stellar Business" :
        Settings.GetString("hostingNeeds") == "small-site" ? "Stellar Plus" : "Stellar";

    private static AvailabilityQuote Quote(JsonElement response, string domain)
    {
        if (!response.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return new("unexpectedError", null, "USD", null);
        var item = results.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("domain", out var name) &&
            string.Equals(name.GetString(), domain, StringComparison.OrdinalIgnoreCase));
        if (item.ValueKind != JsonValueKind.Object) return new("unexpectedError", null, "USD", null);
        var result = item.TryGetProperty("result", out var state) ? state.GetString() ?? "unexpectedError" : "unexpectedError";
        if (!item.TryGetProperty("price", out var price)) return new(result, null, "USD", null);
        return new(result, price.TryGetProperty("amount", out var amount) && amount.TryGetDecimal(out var value) ? value : null,
            price.TryGetProperty("currency", out var currency) ? currency.GetString() ?? "USD" : "USD",
            price.TryGetProperty("pricedYears", out var years) && years.TryGetInt32(out var count) ? count : null);
    }

    private static string? ExtractDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = DomainPattern().Match(value);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? FindCheckoutAction(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("checkoutAction", out var action) && action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("uri", out var uri)) return uri.GetString();
            if (element.TryGetProperty("checkoutUrl", out var checkout) && checkout.ValueKind == JsonValueKind.String)
                return checkout.GetString();
            foreach (var property in element.EnumerateObject())
                if (FindCheckoutAction(property.Value) is { } found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                if (FindCheckoutAction(child) is { } found) return found;
        return null;
    }

    private static bool HasOmittedRecords(JsonElement response)
    {
        if (!response.TryGetProperty("omittedRecords", out var omitted)) return false;
        return omitted.ValueKind switch
        {
            JsonValueKind.Array => omitted.GetArrayLength() > 0,
            JsonValueKind.Number => !omitted.TryGetInt32(out var count) || count > 0,
            JsonValueKind.True => true,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(omitted.GetString()),
            _ => false
        };
    }

    private static bool IsManagedWebRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) return false;
        var type = record.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
        var name = record.TryGetProperty("name", out var nameNode) ? nameNode.GetString() :
            record.TryGetProperty("host", out var hostNode) ? hostNode.GetString() : null;
        return string.Equals(type, "A", StringComparison.OrdinalIgnoreCase) && name is "@" or "" ||
               string.Equals(type, "CNAME", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(name, "www", StringComparison.OrdinalIgnoreCase);
    }
    [GeneratedRegex(@"(?<![a-z0-9-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?![a-z0-9-])", RegexOptions.IgnoreCase)]
    private static partial Regex DomainPattern();
    private sealed record AvailabilityQuote(string Result, decimal? Amount, string Currency, int? PricedYears);
}

public static class StarterSite
{
    public static string Render(string businessName, string message, out string marker)
    {
        marker = Marker(businessName, message);
        using var stream = typeof(StarterSite).Assembly.GetManifestResourceStream(
            "CSweet.Agent.InfrastructureEngineer.Namecheap.Assets.Starter.index.template.html")
            ?? throw new InvalidOperationException("The bundled starter template is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("{{BUSINESS_NAME}}", WebUtility.HtmlEncode(businessName), StringComparison.Ordinal)
            .Replace("{{MESSAGE}}", WebUtility.HtmlEncode(message), StringComparison.Ordinal)
            .Replace("{{MARKER}}", marker, StringComparison.Ordinal);
    }

    public static string Marker(string businessName, string message) =>
        $"csweet-starter:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{businessName}\n{message}"))).ToLowerInvariant()[..20]}";
}
