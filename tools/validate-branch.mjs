#!/usr/bin/env node

// Run ROS validation the way CI runs it.
//
// `./ros validate` on its own compares the working tree against HEAD. CI sets
// ROS_BASE_REF to the branch point, which makes `gitPaths` include everything
// the branch changed — a strictly larger set, and the one that matters for a
// pull request.
//
// The difference is not academic. Three files went the whole branch without
// work-item attribution and every local run said "validation passed", because
// they had been committed long before and no local check ever looked back
// past HEAD. CI caught it on the twenty-ninth commit.
//
// A local gate that is weaker than the CI gate is not a gate. This computes
// the same base and runs the same check.

import { execFileSync } from 'node:child_process'

const run = (command, args) =>
  execFileSync(command, args, { encoding: 'utf8' }).trim()

// The branch point against the default branch, matching what CI passes.
// Falls back to the remote's HEAD if origin/main is not present locally.
const baseRef = (() => {
  for (const candidate of ['origin/main', 'main']) {
    try {
      return run('git', ['merge-base', 'HEAD', candidate])
    } catch {
      // Try the next candidate.
    }
  }

  return null
})()

if (!baseRef) {
  console.error('could not determine a branch point against origin/main or main')
  process.exit(1)
}

console.log(`validating against the branch point ${baseRef.slice(0, 12)}`)

try {
  console.log(
    execFileSync('./ros', ['validate'], {
      encoding: 'utf8',
      env: { ...process.env, ROS_BASE_REF: baseRef }
    }).trim()
  )
} catch (error) {
  // ROS prints its own errors and repair hints; pass them through and fail.
  process.stdout.write(error.stdout ?? '')
  process.stderr.write(error.stderr ?? '')
  process.exit(1)
}
