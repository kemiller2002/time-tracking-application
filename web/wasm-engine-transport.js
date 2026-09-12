// Loads the published .NET WASM runtime and forwards each message as JSON to
// the Ledger.Wasm shim's single [JSExport] method, parsing the JSON it
// returns. It never inspects the message contents — pure mechanics.

const WASM_FRAMEWORK_BASE = "/f-sharp/src/Ledger.Wasm/bin/Release/net10.0/publish/wwwroot/_framework";

export class WasmEngineTransport {
  #exports = null;

  async start() {
    const { dotnet } = await import(`${WASM_FRAMEWORK_BASE}/dotnet.js`);
    const { getAssemblyExports, getConfig } = await dotnet.withDiagnosticTracing(false).create();
    const config = getConfig();
    const exports = await getAssemblyExports(config.mainAssemblyName);

    if (!exports.LedgerWasm || typeof exports.LedgerWasm.Dispatch !== "function") {
      throw new Error("LedgerWasm.Dispatch export not found — rebuild f-sharp/src/Ledger.Wasm and republish.");
    }

    this.#exports = exports;
  }

  async dispatch(message) {
    if (!this.#exports) {
      throw new Error("WasmEngineTransport.dispatch() called before start()");
    }

    const responseJson = this.#exports.LedgerWasm.Dispatch(JSON.stringify(message));
    return JSON.parse(responseJson);
  }
}
