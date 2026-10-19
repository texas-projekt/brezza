# ADR 0012: Collect test coverage with coverlet

- Status: Accepted
- Date: 2026-10-19

## Context

The test suite reports pass/fail and TRX results but records no line or branch coverage. That makes it
hard to see which Core and Persistence paths the tests exercise, and a coverage threshold cannot be
introduced responsibly before a baseline exists.

## Decision

Add `coverlet.collector` 6.0.4 to the test project and run the Unit stage with
`--collect:"XPlat Code Coverage"`. The collector emits Cobertura XML under
`artifacts/test-results/`, and CI uploads that directory with the TRX files.

The Unit stage enforces a floor of 85% line coverage and 70% branch coverage. It reads only the
Cobertura files written by the current run and applies the floor to each target framework, so a
report that silently disappears also fails the stage. The floors sit below the measured baseline
(about 88% line and 76% branch) with enough room for ordinary refactoring while still catching a
material regression.

## Consequences

- Coverage collection is test-only; no runtime dependency is added to the shipped application.
- Both target frameworks are measured in the same command.
- The package is pinned and covered by `packages.lock.json`.
- A flaky test now also affects the coverage artifact; the TRX remains the pass/fail source.
