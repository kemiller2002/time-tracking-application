#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ID_RE =
  /^(RP|JR|EV|HY|TH|EX|DF|CN|GL|MS)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-(?:[0-9]{4}|[A-F0-9]{4})$/;
const REFERENCE_FIELDS = new Set([
  "contradicts",
  "contradicting_evidence",
  "depends_on",
  "derived_from",
  "evidence_ids",
  "hypothesis_ids",
  "related_documents",
  "related_mission",
  "related_package",
  "related_theories",
  "supporting_evidence",
  "supports",
  "superseded_by",
  "supersedes",
  "tests_hypotheses",
  "theory_ids"
]);
const ALLOWED_STATUS = {
  DF: new Set(["draft", "review", "accepted", "superseded", "withdrawn"]),
  EV: new Set(["draft", "review", "accepted", "superseded", "withdrawn"]),
  EX: new Set(["proposed", "active", "blocked", "completed", "cancelled"]),
  HY: new Set(["proposed", "active", "supported", "rejected", "superseded", "withdrawn"]),
  MS: new Set(["proposed", "approved", "active", "blocked", "completed", "cancelled", "archived"]),
  RP: new Set([
    "draft",
    "review",
    "accepted",
    "canonical",
    "deprecated",
    "archived",
    "superseded",
    "withdrawn"
  ]),
  TH: new Set(["candidate", "supported", "established", "challenged", "superseded", "rejected"])
};
const CONFIDENCE = new Set(["very-low", "low", "medium", "high", "very-high"]);
const KIND_CONFIG = {
  decisions: ["research/decisions", "registries/decisions.json", "DF"],
  evidence: ["research/evidence", "registries/evidence.json", "EV"],
  experiments: ["research/experiments", "registries/experiments.json", "EX"],
  hypotheses: ["research/hypotheses", "registries/hypotheses.json", "HY"],
  journals: ["research/journals", "registries/journals.json", "JR"],
  missions: ["missions", "registries/missions.json", "MS"],
  "research-packages": ["research/packages", "registries/research-packages.json", "RP"],
  theories: ["research/theories", "registries/theories.json", "TH"]
};

function scalar(raw) {
  const value = raw.trim();
  if (!value) return "";
  if (value === "[]") return [];
  if (value === "{}") return {};
  if (value.startsWith("[") && value.endsWith("]")) {
    const inner = value.slice(1, -1).trim();
    if (!inner) return [];
    return inner.split(",").map((item) => item.trim().replace(/^['"]|['"]$/g, ""));
  }
  if (["true", "false"].includes(value.toLowerCase())) return value.toLowerCase() === "true";
  if (/^-?\d+(?:\.\d+)?$/.test(value)) return Number(value);
  return value.replace(/^['"]|['"]$/g, "");
}

export function parseFrontMatter(text) {
  const lines = text.split(/\r?\n/);
  if (lines[0]?.trim() !== "---") throw new Error("missing opening '---'");
  const end = lines.findIndex((line, index) => index > 0 && line.trim() === "---");
  if (end < 0) throw new Error("missing closing '---'");
  const result = {};
  const stack = [{ indent: -1, value: result }];
  for (let index = 1; index < end; index += 1) {
    const raw = lines[index];
    if (!raw.trim() || raw.trimStart().startsWith("#")) continue;
    const indent = raw.length - raw.trimStart().length;
    const stripped = raw.trim();
    while (stack.at(-1).indent >= indent) stack.pop();
    const parent = stack.at(-1).value;
    if (stripped.startsWith("- ")) {
      if (!Array.isArray(parent)) throw new Error(`line ${index + 1}: list item has no list field`);
      parent.push(scalar(stripped.slice(2)));
      continue;
    }
    const separator = stripped.indexOf(":");
    if (separator < 1 || Array.isArray(parent)) {
      throw new Error(`line ${index + 1}: expected 'field: value'`);
    }
    const key = stripped.slice(0, separator).trim();
    const rawValue = stripped.slice(separator + 1);
    if (rawValue.trim()) {
      parent[key] = scalar(rawValue);
      continue;
    }
    const next = lines[index + 1];
    const nextIndent = next ? next.length - next.trimStart().length : -1;
    const child = next && nextIndent > indent && next.trim().startsWith("- ") ? [] : {};
    parent[key] = child;
    stack.push({ indent, value: child });
  }
  return result;
}

function walkMarkdown(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return walkMarkdown(target);
    return entry.isFile() && entry.name.endsWith(".md") && !entry.name.startsWith(".") ? [target] : [];
  });
}

function artifactFiles(root) {
  const files = new Set();
  for (const [directory] of Object.values(KIND_CONFIG)) {
    for (const file of walkMarkdown(path.join(root, directory))) files.add(file);
  }
  return [...files].sort();
}

function loadArtifacts(root) {
  const artifacts = [];
  const findings = [];
  for (const file of artifactFiles(root)) {
    const relative = path.relative(root, file).split(path.sep).join("/");
    try {
      const metadata = parseFrontMatter(fs.readFileSync(file, "utf8"));
      artifacts.push({
        file,
        relative,
        metadata,
        id: String(metadata.id ?? metadata.identifier ?? "")
      });
    } catch (error) {
      findings.push({ path: relative, field: "front_matter", message: error.message });
    }
  }
  return { artifacts, findings };
}

function prefix(identifier) {
  return identifier.includes("-") ? identifier.split("-", 1)[0] : "";
}

function references(value) {
  if (typeof value === "string") return ID_RE.test(value) ? [value] : [];
  if (Array.isArray(value)) return value.filter((item) => typeof item === "string" && ID_RE.test(item));
  return [];
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, stable(value[key])]));
  }
  return value;
}

function renderedRegistries(root, artifacts) {
  const rendered = new Map();
  for (const [, registry, artifactPrefix] of Object.values(KIND_CONFIG)) {
    const entries = artifacts
      .filter((artifact) => prefix(artifact.id) === artifactPrefix)
      .map((artifact) => {
        const entry = { ...artifact.metadata, id: artifact.id, path: artifact.relative };
        delete entry.identifier;
        return stable(entry);
      })
      .sort((a, b) => a.id.localeCompare(b.id));
    rendered.set(path.join(root, registry), `${JSON.stringify(entries, null, 2)}\n`);
  }
  return rendered;
}

function registryFindings(root, artifacts) {
  const findings = [];
  for (const [file, expected] of renderedRegistries(root, artifacts)) {
    const actual = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
    if (actual !== expected) {
      findings.push({
        path: path.relative(root, file).split(path.sep).join("/"),
        field: "",
        message: "registry is stale; run 'ros registry build'"
      });
    }
  }
  return findings;
}

export function validate(root, { checkRegistries = true } = {}) {
  const loaded = loadArtifacts(root);
  const findings = [...loaded.findings];
  const byId = new Map();
  for (const artifact of loaded.artifacts) {
    if (!artifact.id) {
      findings.push({ path: artifact.relative, field: "id", message: "required field is missing" });
      continue;
    }
    if (!ID_RE.test(artifact.id)) {
      findings.push({ path: artifact.relative, field: "id", message: `invalid identifier '${artifact.id}'` });
    }
    if (!byId.has(artifact.id)) byId.set(artifact.id, []);
    byId.get(artifact.id).push(artifact);
    if (!artifact.metadata.title) {
      findings.push({ path: artifact.relative, field: "title", message: "required field is missing" });
    }
    if (!path.basename(artifact.file).startsWith(`${artifact.id}--`)) {
      findings.push({
        path: artifact.relative,
        field: "id",
        message: `filename must start with '${artifact.id}--'`
      });
    }
    const kind = prefix(artifact.id);
    const status = artifact.metadata.status;
    if (status && ALLOWED_STATUS[kind] && !ALLOWED_STATUS[kind].has(status)) {
      findings.push({ path: artifact.relative, field: "status", message: `'${status}' is not allowed for ${kind}` });
    }
    const confidence = artifact.metadata.confidence;
    if (typeof confidence === "string" && !CONFIDENCE.has(confidence)) {
      findings.push({ path: artifact.relative, field: "confidence", message: `unknown label '${confidence}'` });
    }
  }
  for (const [identifier, records] of byId) {
    if (records.length > 1) {
      const paths = records.map((record) => record.relative).join(", ");
      for (const record of records) {
        findings.push({
          path: record.relative,
          field: "id",
          message: `duplicate '${identifier}' also in ${paths}`
        });
      }
    }
  }
  const known = new Set(byId.keys());
  for (const artifact of loaded.artifacts) {
    for (const field of REFERENCE_FIELDS) {
      for (const target of references(artifact.metadata[field])) {
        if (!known.has(target)) {
          findings.push({ path: artifact.relative, field, message: `broken reference '${target}'` });
        }
        if (target === artifact.id && ["supersedes", "superseded_by"].includes(field)) {
          findings.push({ path: artifact.relative, field, message: "artifact cannot supersede itself" });
        }
      }
    }
    for (const target of references(artifact.metadata.supersedes)) {
      const reciprocal = byId.get(target)?.[0];
      if (reciprocal && !references(reciprocal.metadata.superseded_by).includes(artifact.id)) {
        findings.push({ path: artifact.relative, field: "supersedes", message: `'${target}' is not reciprocal` });
      }
    }
    for (const target of references(artifact.metadata.superseded_by)) {
      const reciprocal = byId.get(target)?.[0];
      if (reciprocal && !references(reciprocal.metadata.supersedes).includes(artifact.id)) {
        findings.push({ path: artifact.relative, field: "superseded_by", message: `'${target}' is not reciprocal` });
      }
    }
  }
  if (checkRegistries) findings.push(...registryFindings(root, loaded.artifacts));
  return findings.sort((a, b) =>
    [a.path, a.field, a.message].join("\0").localeCompare([b.path, b.field, b.message].join("\0"))
  );
}

export function buildRegistries(root, { dryRun = false } = {}) {
  const loaded = loadArtifacts(root);
  if (loaded.findings.length) return { changed: 0, findings: loaded.findings };
  let changed = 0;
  for (const [file, content] of renderedRegistries(root, loaded.artifacts)) {
    const actual = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
    if (actual === content) continue;
    changed += 1;
    console.log(`${dryRun ? "WOULD WRITE" : "WROTE"} ${path.relative(root, file).split(path.sep).join("/")}`);
    if (!dryRun) {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, content, "utf8");
    }
  }
  return { changed, findings: [] };
}

function renderFinding(finding) {
  const location = finding.field ? `${finding.path}:${finding.field}` : finding.path;
  return `${location}: ${finding.message}`;
}

function parseCli(argv) {
  let root = process.cwd();
  const args = [...argv];
  const rootIndex = args.indexOf("--root");
  if (rootIndex >= 0) {
    if (!args[rootIndex + 1]) throw new Error("--root requires a value");
    root = path.resolve(args[rootIndex + 1]);
    args.splice(rootIndex, 2);
  }
  return { root: path.resolve(root), args };
}

export function main(argv) {
  try {
    const { root, args } = parseCli(argv);
    if (args[0] === "validate") {
      const findings = validate(root);
      if (findings.length) {
        for (const finding of findings) console.error(`ERROR ${renderFinding(finding)}`);
        console.error(`validation failed with ${findings.length} error(s)`);
        return 1;
      }
      console.log("validation passed");
      return 0;
    }
    if (args[0] === "registry" && args[1] === "build") {
      const result = buildRegistries(root, { dryRun: args.includes("--dry-run") });
      if (result.findings.length) {
        for (const finding of result.findings) console.error(`ERROR ${renderFinding(finding)}`);
        return 1;
      }
      console.log(`${result.changed} registry file(s) ${args.includes("--dry-run") ? "would change" : "changed"}`);
      return 0;
    }
    if (args[0] === "registry" && args[1] === "check") {
      const loaded = loadArtifacts(root);
      const findings = [...loaded.findings, ...registryFindings(root, loaded.artifacts)];
      if (findings.length) {
        for (const finding of findings) console.error(`ERROR ${renderFinding(finding)}`);
        return 1;
      }
      console.log("registries are current");
      return 0;
    }
    console.error("Usage: ros [--root PATH] validate | registry build [--dry-run] | registry check");
    return 2;
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
