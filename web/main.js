import { WasmEngineTransport } from "./wasm-engine-transport.js";
import { DomBindings } from "./dom-bindings.js";

const bindings = new DomBindings(new WasmEngineTransport());
await bindings.start();
