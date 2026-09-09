# Namecheap Infrastructure Engineer

This standalone C-Sweet agent bootstraps and operates a Namecheap domain and Shared Hosting account
for a business whose only employee is its human CEO/owner and this agent.

## Bootstrap journey

The `assistant.converse.v1` capability guides the CEO through Namecheap MCP OAuth, current domain
availability and price, a hash-bound fiscal approval, Namecheap-hosted checkout, ownership
verification, Shared Hosting selection and checkout, secure hosting activation, DNS, the bundled
Coming Soon page, and public HTTPS verification. Provider writes never execute from chat text.

Passwords, API keys, registration contacts, and SFTP details are collected by platform-owned secure
forms. The agent receives opaque references only. The optional legacy Namecheap API is separately
vaulted and production access remains administrator-enabled with fixed-egress IPv4 whitelisting.

The starter page is a deterministic infrastructure smoke test. After HTTPS succeeds, the agent
publishes a deployment contract; future application releases are outside this role. Weekly
reconciliation remains responsible for domain, DNS, registrar, SSL, hosting access, drift, expiry,
and incident information.

## Boundaries

- Role: `infrastructure-engineer`, specialization: `namecheap`, profile: `individual-contributor.v1`
- Exact MCP/API operations and the SFTP target are declared in `csweet-plugin.json`.
- SFTP is restricted to approved Namecheap Shared Hosting suffixes, port `21098`, `public_html`, and
  probe/list/stat/upload. There is no shell or arbitrary path capability.
- Every write is proposed through a native approval bound to the exact payload hash.
- A checkout-start result is never treated as purchase or hosting activation confirmation.

## Develop

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.InfrastructureEngineer.Namecheap -- --self-test
```

The unit tests need no provider credentials. Live acceptance requires an explicitly configured test
organization and Namecheap account. Built with `CSweet.Agent.SDK` 3.40.0.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.
