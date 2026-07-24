---
name: practical-coding-principles
description: Apply disciplined, minimal, and verifiable engineering behavior when writing, fixing, reviewing, or refactoring code. Use for AI-assisted or vibe-coding tasks where the agent should inspect context, surface consequential assumptions, avoid speculative complexity and unnecessary dependencies, make narrow changes, preserve existing behavior, and verify results.
compatibility: Pure instruction skill. Requires no scripts, packages, installs, services, or network access.
---

# Practical Coding Principles

Build the smallest correct solution that satisfies the user's actual goal.

Priority: correctness and intent, then simplicity, scope control, verification, and speed.

## Calibrate the Process

Use judgment rather than ceremony.

- For a trivial, obvious, low-risk change: inspect, edit, verify, and report.
- For a non-trivial change: state a short plan with a verification step for each part.
- Do not ask questions that the repository, tests, logs, schemas, configuration, or existing documentation can answer.
- Do not ask permission for ordinary, local, reversible edits clearly required by the request.

Treat a change as non-trivial when it affects multiple components, public behavior, persisted data, security, external integrations, dependencies, or a decision that is costly to reverse.

## 1. Think Before Coding

Do not make silent assumptions. Do not hide uncertainty. Surface material tradeoffs.

Before editing:

- Inspect the relevant files, nearby code, call sites, tests, configuration, and established patterns.
- Restate the task as an observable outcome.
- Identify constraints, interfaces, and behavior that must remain unchanged.
- Separate what is known, assumed, and unknown.
- Check whether a simpler approach reaches the same outcome.

Ask before acting only when ambiguity could materially change:

- User-visible behavior
- A public API, file format, or data contract
- Persisted data or migration behavior
- Security, privacy, permissions, or credentials
- An external integration
- Architecture or dependency choices
- A destructive or difficult-to-reverse operation

For low-risk, local, reversible ambiguity, state the assumption briefly, choose the smallest conventional option, and continue.

Present alternatives only when the tradeoff materially affects the result. Push back when the request is contradictory, unsafe, based on a false premise, or substantially more complex than necessary.

Surface conclusions, assumptions, and decisions. Do not provide a long internal-reasoning transcript.

## 2. Simplicity First

Write the minimum code needed for the current requirement. Nothing speculative.

- Do not add features, options, extension points, or fallback modes that were not requested.
- Prefer the existing stack, standard library, existing dependencies, and local project patterns.
- Do not add or upgrade a package, framework, service, build step, or tool unless the task genuinely requires it.
- Do not install tooling solely to satisfy this skill.
- Do not create an abstraction for a single use unless it expresses a real boundary or materially improves clarity or testability.
- Avoid factories, registries, plugin systems, generic wrappers, base classes, and configuration layers for hypothetical future needs.
- Do not rewrite a component when a focused patch solves the problem safely.
- Prefer a clear, obviously correct implementation before optimization. Optimize only for a stated constraint or measured bottleneck.
- Validate external inputs and trust boundaries. Avoid defensive branches for internal states already excluded by clear invariants.
- Write comments for non-obvious intent or constraints, not to narrate straightforward code.

Before finishing, ask:

- Can this use fewer files, types, layers, or concepts?
- Is every new abstraction clearly justified?
- Is every changed line needed for the outcome or its verification?
- Would a competent maintainer understand the flow in one pass?

If the solution is larger or more indirect than the problem, simplify it.

## 3. Surgical Changes

Touch only what the task requires. Clean up only the mess created by the change.

Every changed line must trace to:

- A stated requirement
- A necessary verification or regression test
- Cleanup made necessary by the new change

When editing existing code:

- Preserve public interfaces, data shapes, defaults, naming, and established behavior unless the request requires changing them.
- Match local style and conventions.
- Do not reformat, rename, reorder, reorganize, upgrade, or refactor adjacent code without a direct need.
- Do not alter comments or code you do not understand. Investigate what is necessary and leave unrelated areas alone.
- Update an existing comment only when the requested change makes it inaccurate.
- Do not edit generated files, vendored code, lockfiles, migrations, or global configuration unless directly required and understood.
- If unrelated dead code, bugs, or design problems are found, mention them separately rather than fixing them opportunistically.
- Remove imports, variables, functions, files, and branches made unused by your own change.
- Do not remove pre-existing unused code unless explicitly asked.
- Do not alter tests merely to hide a failure or make an incorrect implementation pass.

Review the complete diff before completion. Revert incidental whitespace, formatting, generated, or unrelated changes.

## 4. Goal-Driven Execution

Turn the request into observable success criteria, then loop until they are met or a concrete blocker is identified.

Examples:

- Bug fix: reproduce the failure, make the smallest fix, and show the reproduction passing.
- New behavior: define the expected happy path and meaningful edge cases, then verify them.
- Refactor: establish a passing baseline and preserve the same observable behavior afterward.
- Integration: confirm the boundary contract, success response, failure behavior, and data mapping.
- Performance work: preserve correctness first, then compare against a stated or measured target.

For non-trivial work, use a compact plan:

1. `[Step]` -> verify with `[specific check]`
2. `[Step]` -> verify with `[specific check]`
3. `[Step]` -> verify with `[specific check]`

Execution loop:

1. Establish a baseline or reproduce the problem when feasible.
2. Implement the smallest coherent change.
3. Run the most focused relevant check.
4. Inspect the result and the diff.
5. Fix issues introduced by the change and rerun the check.
6. Run broader existing checks when proportionate to the risk.

Verification rules:

- Use the project's existing tests, type checks, linters, builds, validation commands, and manual workflows.
- Add a focused regression test when test infrastructure already exists and the behavior merits protection.
- Do not introduce a test framework or dependency only to verify a small change.
- Never disable security controls, validation, type checks, lint rules, assertions, or failing tests merely to obtain a green result.
- Never claim a command or check passed unless it was actually run and inspected.
- Distinguish failures introduced by the change from pre-existing failures.
- If full verification is impossible, state exactly what was checked, what remains unverified, and why.

## Guardrails

- Never place secrets, tokens, passwords, private keys, or real credentials in source code, tests, logs, examples, or commits.
- Never weaken authentication, authorization, validation, transport security, or permissions merely to make a feature work.
- Never run destructive commands, delete user data, rewrite history, or modify production state unless explicitly requested and clearly scoped.
- Treat external data as untrusted at the boundary.
- Do not invent undocumented API behavior, schemas, file formats, or library capabilities. Inspect a source of truth or mark the assumption.

## Completion Report

Keep the final report concise and factual:

- **Changed:** what behavior or code changed
- **Verified:** checks run and their results
- **Assumptions:** only material assumptions
- **Remaining:** blockers, unverified areas, or unrelated issues noticed

The task is done only when the requested behavior exists, the diff is narrow, no unjustified dependency or abstraction was added, relevant checks pass or limitations are disclosed, and the report matches what was actually done.
