// A minimal, first-party replacement for `@echelon-foundry/typescript-wasm-kernel`'s
// `BrowserKernel` (which cannot be depended on here — see docs/DOMAIN-REQUIREMENTS.md's
// implementation notes and the plan this repo was built from: the package is a
// `file:../typescript-wasm-kernel` reference to a sibling directory that does not
// exist in any checkout). This module re-implements only the subset of `data-*`
// binding behavior this app's `web/index.html` actually uses.
//
// It never inspects or decides anything about message contents — it renders
// whatever `view` the engine returns, dispatches whatever `event` the DOM raises,
// and fulfils `effects` (Storage only) generically. All business meaning stays in
// the WASM engine on the other side of `transport.dispatch()`.

const truthy = (value) => {
  if (Array.isArray(value)) return value.length > 0;
  return value === true || (typeof value === "string" && value !== "") || (typeof value === "number" && value !== 0);
};

export class DomBindings {
  #transport;
  #root;
  #view = {};
  #rowSets = new WeakMap(); // <template> -> Map(rowKey -> renderedElement)

  constructor(transport, root = document) {
    this.#transport = transport;
    this.#root = root;
  }

  async start() {
    await this.#transport.start();
    this.#bindDelegatedListeners();
    const response = await this.#transport.dispatch({ kind: "Initialize", protocolVersion: 1, capabilities: ["Storage"] });
    await this.#apply(response);
  }

  async dispatch(name, key, value) {
    const event = { name, key: key ?? null, value: value ?? null };
    const response = await this.#transport.dispatch({ kind: "Event", event });
    await this.#apply(response);
  }

  async #apply(response) {
    this.#view = response.view ?? {};
    this.render();
    await this.#runEffects(response.effects ?? []);
  }

  render() {
    this.#renderScope(this.#root, this.#view);
  }

  // --- rendering ------------------------------------------------------------

  #renderScope(scope, view) {
    for (const el of scope.querySelectorAll("[data-text]")) {
      if (this.#owningEach(el, scope)) continue;
      const key = el.getAttribute("data-text");
      if (Object.prototype.hasOwnProperty.call(view, key)) el.textContent = String(view[key]);
    }

    for (const el of scope.querySelectorAll("[data-key-from]")) {
      if (this.#owningEach(el, scope)) continue;
      const key = el.getAttribute("data-key-from");
      el.dataset.dispatchKey = view[key] != null ? String(view[key]) : "";
    }

    for (const el of scope.querySelectorAll("[data-if]")) {
      if (this.#owningEach(el, scope)) continue;
      const key = el.getAttribute("data-if");
      const show = truthy(view[key]);
      if (el.tagName === "TEMPLATE") {
        this.#renderTemplateIf(el, show, view);
      } else {
        el.hidden = !show;
      }
    }

    for (const el of scope.querySelectorAll("[data-each]")) {
      if (this.#owningEach(el, scope)) continue;
      this.#renderEach(el, view);
    }
  }

  /** True when `el` sits inside a *different* `data-each` template's rendered
   * rows than the scope currently being rendered — those elements are handled
   * by `#renderEach`'s own row-scoped pass, not the outer pass. */
  #owningEach(el, scope) {
    const each = el.closest("[data-each-row]");
    return each !== null && each !== scope;
  }

  #renderTemplateIf(template, show, view) {
    const markerKey = "__ifRendered";
    if (show) {
      if (!template[markerKey]) {
        const fragment = template.content.cloneNode(true);
        template[markerKey] = Array.from(fragment.children);
        template.after(fragment);
      }
      for (const el of template[markerKey]) this.#renderScope(el, view);
    } else if (template[markerKey]) {
      for (const el of template[markerKey]) el.remove();
      template[markerKey] = null;
    }
  }

  #renderEach(template, view) {
    const key = template.getAttribute("data-each");
    const keyField = template.getAttribute("data-key") ?? "id";
    const rows = Array.isArray(view[key]) ? view[key] : [];
    const existing = this.#rowSets.get(template) ?? new Map();
    const next = new Map();
    let anchor = template;

    for (const row of rows) {
      const rowKey = String(row[keyField]);
      let el = existing.get(rowKey);
      if (el) {
        existing.delete(rowKey);
      } else {
        const fragment = template.content.cloneNode(true);
        el = fragment.firstElementChild;
        el.setAttribute("data-each-row", "");
      }
      el.dataset.dispatchKey = rowKey;
      anchor.after(el);
      anchor = el;
      this.#renderScope(el, row);
      next.set(rowKey, el);
    }

    for (const stale of existing.values()) stale.remove();
    this.#rowSets.set(template, next);
  }

  // --- dispatch ---------------------------------------------------------------

  #bindDelegatedListeners() {
    for (const on of ["submit", "change", "click"]) {
      this.#root.addEventListener(on, (domEvent) => this.#handleDomEvent(on, domEvent));
    }
  }

  #handleDomEvent(on, domEvent) {
    const target = domEvent.target;
    const trigger = target.closest(`[data-event][data-on="${on}"]`) ?? (on === "submit" ? target.closest("form[data-event]:not([data-on])") : null);
    if (!trigger) return;
    if (on === "submit") domEvent.preventDefault();

    const name = trigger.getAttribute("data-event");
    const key = this.#resolveKey(trigger);
    const value = this.#resolveValue(trigger, on, target);
    this.dispatch(name, key, value);
  }

  #resolveKey(trigger) {
    if (trigger.hasAttribute("data-event-key")) return trigger.getAttribute("data-event-key");
    const scoped = trigger.closest("[data-dispatch-key]");
    return scoped ? scoped.dataset.dispatchKey : null;
  }

  #resolveValue(trigger, on, target) {
    if (trigger.hasAttribute("data-event-value")) return trigger.getAttribute("data-event-value");
    if (on === "submit") return null;
    if (target.type === "checkbox") return target.checked ? "on" : "off";
    if ("value" in target) return target.value;
    return null;
  }

  // --- effects ------------------------------------------------------------------

  async #runEffects(effects) {
    for (const effect of effects) {
      if (effect.kind !== "Storage") continue; // Http effects are not fulfilled by this MVP bridge.
      let outcome;
      try {
        if (effect.operation === "get") {
          const value = window.localStorage.getItem(effect.key);
          outcome = { kind: "Success", value: value ?? null };
        } else if (effect.operation === "set") {
          window.localStorage.setItem(effect.key, effect.value);
          outcome = { kind: "Success", value: null };
        } else {
          window.localStorage.removeItem(effect.key);
          outcome = { kind: "Success", value: null };
        }
      } catch (error) {
        outcome = { kind: "Failure", reason: String(error && error.message ? error.message : error) };
      }

      const response = await this.#transport.dispatch({
        kind: "EffectResult",
        result: { correlationId: effect.correlationId, kind: "StorageResult", outcome },
      });
      await this.#apply(response);
    }
  }
}
