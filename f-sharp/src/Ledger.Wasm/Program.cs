using System.Runtime.InteropServices.JavaScript;

// Entry point required by the WebAssembly SDK; the WASM module is driven
// entirely through the [JSExport] below, not through this Main running any
// logic of its own.
return;

// SDE Tier 4 (Host / External Effects): owns WASM/browser interop
// (.sde/architecture/FOUR-TIER-ARCHITECTURE.md). Pure marshalling shim —
// this class makes no decisions, it forwards a JSON string to
// Ledger.Engine.Dispatch.handle (Tier 3) and returns whatever comes back.
// All application meaning lives in F# (Ledger.Domain + Ledger.Engine).
public partial class LedgerWasm
{
    [JSExport]
    internal static string Dispatch(string messageJson) => Ledger.Engine.Dispatch.handle(messageJson);
}
