# Transport lifecycle probe

This .NET 8 executable **links the production `ManagedHttpTransportClient.cs`**.
It exercises its deadlines, generation isolation, old task cleanup, readiness
failures, session changes, configuration cancellation and project independence.
Health-gate checks require zero raw clients before readiness, bounded waits,
automatic retry, late cancellation isolation, and unmanaged bypass.
The single-thread synchronization pump models Unity's Editor-thread awaits.

Run from the repository root:

```text
dotnet run --project tests/transport-probe/TransportProbe.csproj --configuration Release
```

`UpstreamStubs.cs` supplies only external MCP API shapes and the dependencies of
the unused default constructor. The registration probe and service locator stubs
throw if invoked; tests inject their clients and registration outcomes. The
WebSocket stub checks which retirement API is selected. Lifecycle logic is not
copied into a test implementation.

This does not test Unity compilation, the actual upstream reconnect loop,
WebSocket handshakes, tool discovery, HTTP registration APIs, or Launcher watchdog
ticks. Those are covered separately by the licensed Unity fixtures and
`tools/verify_recovery.py` with real MCP and two isolated Unity projects. The
cross-platform CI result does not claim native Unity acceptance on each OS.
