# MCP RID Packaging Resolution

MCP-2 was audited in the separate worktree `C:\DocxHeaderExtractor-mcp2` at
base revision `30befaad58ea5c73e8ebd56b051982e4f117a403`. The worktree was
clean before the audit.

The exact VERIFY-2 failure was reproduced. A default Release build emitted
`bin/Release/net9.0/dhx-mcp.dll` and passed. A `win-x64` build emitted the
same host under `bin/Release/net9.0/win-x64/`, while the old test helper only
searched the non-RID path. The RID build and publish output both contained
the DLL, executable, deps file, and runtimeconfig file.

The first divergence is therefore `FindMcpDll` test lookup, not MCP
production logic, project-reference copying, or packaging. The minimal fix
updates the test helper to search the runtime RID subdirectory and then fall
back to the default output directory. It does not hard-code a machine path or
a single RID.

Verification after the helper change:

- default/no explicit RID MCP test: `1/1 PASS`;
- `RuntimeIdentifier=win-x64` MCP test: `1/1 PASS`;
- `dotnet publish -r win-x64 --self-contained false`: artifact present;
- production code changed: `false`;
- provider calls: `0`.

The change is isolated to `tests/McpIntegrationTests.cs`; SourceFacts, Slim,
RoutePolicy, RFC analyzer, validator, hierarchy, and production host logic are
untouched.
