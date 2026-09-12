using System.Runtime.InteropServices.JavaScript;

// Entry point required by the WebAssembly SDK; the WASM module is driven
// entirely through the [JSExport] below, not through this Main running any
// logic of its own.
return;

// Pure marshalling shim. This class makes no decisions — it forwards a JSON
// string to Ledger.Engine.Dispatch.handle and returns whatever comes back.
// All application meaning lives in F# (Ledger.Domain + Ledger.Engine).
public partial class LedgerWasm
{
    [JSExport]
    internal static string Dispatch(string messageJson) => Ledger.Engine.Dispatch.handle(messageJson);
}
