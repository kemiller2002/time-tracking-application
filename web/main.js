import { WasmEngineTransport } from "./wasm-engine-transport.js";
import { DomBindings } from "./dom-bindings.js";

// Best-effort: an offline-capable shell is a nice-to-have, not a requirement
// for the app to work, so a registration failure (unsupported browser,
// served over plain HTTP) is swallowed rather than blocking startup.
if ("serviceWorker" in navigator) {
  navigator.serviceWorker.register("./service-worker.js").catch(() => {});
}

const bindings = new DomBindings(new WasmEngineTransport());
await bindings.start();
