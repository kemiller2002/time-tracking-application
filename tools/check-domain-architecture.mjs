#!/usr/bin/env node

// Mechanical tier-boundary check for the Time Entry F# domain.
//
// SDE doctrine (.sde/architecture/FOUR-TIER-ARCHITECTURE.md) cites HelixNote's
// `scripts/check-semantic-architecture.sh` as the mechanism that makes the
// Tier 1 boundary structural rather than aspirational. This is that mechanism
// for this repository.
//
// It is deliberately a *static text and project-reference* check, not a build.
// That keeps it runnable with no .NET SDK present, and it catches boundary
// violations the compiler cannot: referencing a forbidden type is legal F#,
// it is just architecturally wrong here. It complements `dotnet build`, it
// does not replace it.
//
// Written in a functional style: pure rule functions over file contents, one
// reduction into a findings list, and a single side effect at the end.

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, extname } from 'node:path';

const ROOT = new URL('..', import.meta.url).pathname;
const DOMAIN = join(ROOT, 'domain');
const BROWSER = join(ROOT, 'browser');

const TIERS = {
  'TimeEntry.Semantic': 1,
  'TimeEntry.Transitions': 2,
  'TimeEntry.Projection': 3,
  // Tier 4 is the only tier permitted to know about serialization, transport
  // and infrastructure. Everything below it is checked against that.
  'TimeEntry.Persistence': 4,
  'TimeEntry.GitHub': 4,
  'TimeEntry.Tests': 4
};

// Identifiers that would mean infrastructure reached a tier that must not know
// about it. Matched as whole words to avoid flagging prose in comments such as
// "not JSON".
const FORBIDDEN = [
  { pattern: /\bHttpClient\b/, what: 'an HTTP client' },
  { pattern: /\bSystem\.Net\b/, what: 'System.Net' },
  { pattern: /\bJsonSerializer\b/, what: 'a JSON serializer' },
  { pattern: /\bNewtonsoft\b/, what: 'Newtonsoft.Json' },
  { pattern: /\bOctokit\b/, what: 'a GitHub SDK' },
  { pattern: /\bSystem\.IO\b/, what: 'System.IO' },
  { pattern: /\bFile\.(?:Read|Write|Open)/, what: 'filesystem access' },
  { pattern: /\bDirectory\.(?:Get|Create|Enumerate)/, what: 'filesystem access' },
  { pattern: /\bSystem\.Net\.Http\b/, what: 'System.Net.Http' },
  { pattern: /\bWebAssembly\b/, what: 'a WASM host API' },
  { pattern: /\bJSRuntime\b/, what: 'a JS interop runtime' },
  { pattern: /\bDateTime\.(?:Now|UtcNow)\b/, what: 'a clock read' },
  { pattern: /\bDateTimeOffset\.(?:Now|UtcNow)\b/, what: 'a clock read' },
  { pattern: /\bGuid\.NewGuid\b/, what: 'nondeterministic identity generation' },
  { pattern: /\bRandom\b/, what: 'nondeterminism' }
];

// Billable time must never be authoritatively computed in floating point
// (TE-R-007). `float`/`double` are banned outright in tiers 1-3.
const FLOAT_TYPES = [
  { pattern: /:\s*float\b/, what: 'a float annotation' },
  { pattern: /:\s*double\b/, what: 'a double annotation' },
  { pattern: /\bSystem\.Double\b/, what: 'System.Double' }
];

// Build output is generated, not authored: it contains AssemblyInfo.fs and
// friends, which would inflate the counts and could report a "violation" in
// code nobody wrote.
const GENERATED = new Set(['bin', 'obj']);

const listFiles = (dir) =>
  readdirSync(dir).flatMap((name) => {
    if (GENERATED.has(name)) return [];
    const full = join(dir, name);
    return statSync(full).isDirectory() ? listFiles(full) : [full];
  });

const tierOf = (path) => {
  const segment = relative(DOMAIN, path).split('/')[0];
  return TIERS[segment];
};

// --- rules: each takes { path, tier, text } and returns findings ------------

const noForbiddenIdentifiers = ({ path, tier, text }) =>
  tier >= 4
    ? []
    : FORBIDDEN.filter(({ pattern }) => pattern.test(stripComments(text))).map(
        ({ what }) => `${path}: tier ${tier} must not reference ${what}`
      );

const noFloatingPoint = ({ path, tier, text }) =>
  tier >= 4
    ? []
    : FLOAT_TYPES.filter(({ pattern }) => pattern.test(stripComments(text))).map(
        ({ what }) => `${path}: tier ${tier} must not use ${what} (TE-R-007)`
      );

// Tier 1 may not depend upward on tier 2 or 3.
const noUpwardDependency = ({ path, tier, text }) => {
  if (tier !== 1) return [];
  const upward = [
    { pattern: /open\s+TimeEntry\.Transitions/, what: 'Tier 2' },
    { pattern: /open\s+TimeEntry\.Projection/, what: 'Tier 3' }
  ];
  return upward
    .filter(({ pattern }) => pattern.test(text))
    .map(({ what }) => `${path}: Tier 1 must not depend on ${what}`);
};

// Comments legitimately discuss what a tier must NOT contain, so strip them
// before matching identifiers.
const stripComments = (text) =>
  text
    .split('\n')
    .map((line) => line.replace(/\/\/.*$/, ''))
    .join('\n');

const RULES = [noForbiddenIdentifiers, noFloatingPoint, noUpwardDependency];

// --- project-reference rules -----------------------------------------------

const projectRules = () => {
  const findings = [];
  const read = (name) =>
    readFileSync(join(DOMAIN, name, `${name}.fsproj`), 'utf8');

  const semantic = read('TimeEntry.Semantic');
  if (/<PackageReference/.test(semantic)) {
    findings.push(
      'TimeEntry.Semantic.fsproj: Tier 1 must reference no NuGet package (TE-R-095)'
    );
  }
  if (/<ProjectReference/.test(semantic)) {
    findings.push('TimeEntry.Semantic.fsproj: Tier 1 must reference no other project');
  }
  if (!/<TargetFramework>netstandard2\.0<\/TargetFramework>/.test(semantic)) {
    findings.push(
      'TimeEntry.Semantic.fsproj: Tier 1 must target netstandard2.0 so it cannot reach infrastructure'
    );
  }

  const transitions = read('TimeEntry.Transitions');
  if (/<PackageReference/.test(transitions)) {
    findings.push('TimeEntry.Transitions.fsproj: Tier 2 must reference no NuGet package');
  }
  const transitionRefs = [...transitions.matchAll(/<ProjectReference Include="([^"]+)"/g)].map(
    (m) => m[1]
  );
  if (transitionRefs.some((ref) => !ref.includes('TimeEntry.Semantic'))) {
    findings.push(
      `TimeEntry.Transitions.fsproj: Tier 2 may only reference Tier 1, found ${transitionRefs.join(', ')}`
    );
  }

  return findings;
};

// --- run -------------------------------------------------------------------

const sources = listFiles(DOMAIN).filter((p) => extname(p) === '.fs');

const fileFindings = sources
  .map((path) => ({
    path: relative(ROOT, path),
    tier: tierOf(path),
    text: readFileSync(path, 'utf8')
  }))
  .flatMap((file) => RULES.flatMap((rule) => rule(file)));

// --- browser boundary rules (TE-R-091, TE-R-092) ----------------------------

// The C# shim exists only to carry [JSExport], which is inert in F#. It must
// forward strings and nothing else, so it may reference the F# kernel and no
// domain assembly: a shim that can see a domain type can start deciding
// things about one.
const shimRules = () => {
  const shim = join(BROWSER, 'TimeEntry.Host', 'Program.cs');
  if (!existsSync(shim)) return [];
  const text = stripComments(readFileSync(shim, 'utf8'));

  return ['TimeEntry.Semantic', 'TimeEntry.Transitions', 'TimeEntry.Projection', 'TimeEntry.Persistence']
    .filter((assembly) => text.includes(assembly))
    .map(
      (assembly) =>
        `browser/TimeEntry.Host/Program.cs: the interop shim must not reference ${assembly} — ` +
        'it forwards strings to TimeEntry.Kernel and makes no decisions (TE-R-091)'
    );
};

// TE-R-091 forbids the browser from computing totals, sorting, filtering or
// converting units. These are the mechanical signatures of doing so.
const bridgeRules = () => {
  const bridge = join(BROWSER, 'TimeEntry.Host', 'main.js');
  if (!existsSync(bridge)) return [];
  const text = stripComments(readFileSync(bridge, 'utf8'));

  const forbidden = [
    { pattern: /\.reduce\s*\(/, what: 'summing (.reduce) — totals are the kernel\'s (TE-R-080)' },
    { pattern: /\.sort\s*\(/, what: 'sorting — ordering is the kernel\'s (TE-R-080)' },
    { pattern: /\.filter\s*\(/, what: 'filtering — the kernel decides visibility (TE-R-080)' },
    { pattern: /\bMath\./, what: 'arithmetic on domain values (TE-R-085)' },
    { pattern: /[*/]\s*(?:60|1000|360000)\b/, what: 'time-unit conversion (TE-R-085)' }
  ];

  return forbidden
    .filter(({ pattern }) => pattern.test(text))
    .map(({ what }) => `browser/TimeEntry.Host/main.js: the bridge must not perform ${what}`);
};

const findings = [...projectRules(), ...shimRules(), ...bridgeRules(), ...fileFindings];

const tierCounts = sources.reduce((counts, path) => {
  const tier = tierOf(path);
  return { ...counts, [tier]: (counts[tier] ?? 0) + 1 };
}, {});

console.log(
  `domain architecture check: ${sources.length} F# files ` +
    `(tier 1: ${tierCounts[1] ?? 0}, tier 2: ${tierCounts[2] ?? 0}, ` +
    `tier 3: ${tierCounts[3] ?? 0}, tier 4 + tests: ${tierCounts[4] ?? 0})`
);

if (findings.length > 0) {
  console.error(`\n${findings.length} architecture violation(s):`);
  findings.forEach((finding) => console.error(`  - ${finding}`));
  process.exit(1);
}

console.log('no tier-boundary violations');
console.log(
  'NOTE: static check only — it does not compile the domain, so it complements ' +
    'dotnet build/test rather than replacing it.'
);
