#!/usr/bin/env node
// Automated Tier 1/2 purity + Tier 3/4 boundary-agreement check
// (manifest.md's "Known gaps" — closed here). Two independent checks:
//
// 1. Domain purity: no file under f-sharp/src/Ledger.Domain/ may reference
//    JSON/WASM/browser/HTTP-transport APIs. Tier 1/2 (.sde/architecture/
//    FOUR-TIER-ARCHITECTURE.md) must stay pure values and pure transitions —
//    that boundary is enforced today only by ProjectReference direction,
//    which stops an accidental import but not a stray `open System.Text.Json`.
// 2. Boundary agreement: every `data-event="Name"` in web/index.html must
//    have a matching string-literal case in Dispatch.fs's routing tables
//    (handleEvent/applyDraftField) — a name present in the HTML but absent
//    from Dispatch.fs is a live Uncoordinated Duplication per
//    .sde/architecture/BOUNDARY-PRESERVATION.md: the browser would dispatch
//    an event the engine silently ignores.
//
// Deliberately dependency-free and fast (plain regex/string scanning, no
// F# parser) — this is a lint-shaped guardrail against drift, not a
// substitute for Ledger.Engine.Specs's behavior tests.

import { readFileSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

const FORBIDDEN_TOKENS = [
  "System.Text.Json",
  "Newtonsoft",
  "JSExport",
  "JSImport",
  "System.Net.Http",
  "HttpClient",
  "localStorage",
  "System.Net.WebSockets",
  "Wasm",
  "Browser",
];

/** Strips `//`-to-end-of-line comments so a comment mentioning a forbidden
 * token (documentation explaining what Domain must NOT depend on, say)
 * doesn't itself trip the check. Not a full F# lexer — doesn't handle `//`
 * inside a string literal — but nothing in this codebase's Domain layer
 * puts one there. */
const stripLineComments = (source) =>
  source
    .split("\n")
    .map((line) => {
      const idx = line.indexOf("//");
      return idx === -1 ? line : line.slice(0, idx);
    })
    .join("\n");

const checkDomainPurity = () => {
  const domainDir = join(repoRoot, "f-sharp/src/Ledger.Domain");
  const fsFiles = readdirSync(domainDir).filter((name) => name.endsWith(".fs"));

  const violations = fsFiles.flatMap((fileName) => {
    const filePath = join(domainDir, fileName);
    const withoutComments = stripLineComments(readFileSync(filePath, "utf8"));
    const lines = withoutComments.split("\n");

    return lines.flatMap((line, index) => {
      const hit = FORBIDDEN_TOKENS.find((token) => line.includes(token));
      return hit
        ? [`${fileName}:${index + 1}: references "${hit}" — Tier 1/2 (Ledger.Domain) must stay pure, no JSON/WASM/browser/HTTP APIs`]
        : [];
    });
  });

  return { name: "Domain purity (Ledger.Domain has no JSON/WASM/browser/HTTP references)", violations };
};

const extractDataEventNames = (html) => {
  const matches = html.matchAll(/data-event="([^"]+)"/g);
  return [...new Set([...matches].map((m) => m[1]))];
};

const checkBoundaryAgreement = () => {
  const html = readFileSync(join(repoRoot, "web/index.html"), "utf8");
  const dispatchSource = stripLineComments(
    readFileSync(join(repoRoot, "f-sharp/src/Ledger.Engine/Dispatch.fs"), "utf8"),
  );

  const eventNames = extractDataEventNames(html);
  const violations = eventNames
    .filter((name) => !dispatchSource.includes(`"${name}"`))
    .map((name) => `web/index.html uses data-event="${name}" with no matching case in Dispatch.fs`);

  return { name: "Boundary agreement (every data-event in web/index.html has a Dispatch.fs case)", violations };
};

const results = [checkDomainPurity(), checkBoundaryAgreement()];
const failed = results.filter((r) => r.violations.length > 0);

for (const result of results) {
  const status = result.violations.length === 0 ? "PASS" : "FAIL";
  console.log(`[${status}] ${result.name}`);
  for (const violation of result.violations) console.log(`  - ${violation}`);
}

process.exit(failed.length === 0 ? 0 : 1);
