# Namecheap Infrastructure Engineer repository instructions

This repository contains one standalone C-Sweet protocol-v2 agent. Its purpose is:

> Bootstraps and operates Namecheap domains, DNS, Shared Hosting, HTTPS, and the deterministic C-Sweet starter site.

## Invariants

- Keep `com.csweet.infrastructure-engineer.namecheap` and version `0.2.0` synchronized between agent code,
  `csweet-plugin.json`, tests, and releases.
- The root manifest is the reviewed authority request. Keep `provides`, `requires`, events,
  configuration, credentials, web access, and UI contributions synchronized with implementation
  and tests.
- Request the minimum authority needed. Manifest declarations never grant access.
- Use typed callbacks and `AgentRuntimeContext.Platform`. Do not implement MCP/JSON-RPC, access
  runtime/workload/lease tokens, connect directly to databases or Docker, or handle provider
  credentials.
- Agent work is delivered at least once. Honor cancellation and use stable domain idempotency keys
  for external mutations.
- Unknown capabilities and events must fail or be ignored safely without leaking sensitive data.

## Verification

Run from the repository root:

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.InfrastructureEngineer.Namecheap -- --self-test
```

Any new capability, grant, event, configuration field, credential, or network rule requires a
manifest update, a README explanation, and tests.
