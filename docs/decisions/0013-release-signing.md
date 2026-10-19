# ADR 0013: No Authenticode signing for release packages

- Status: Accepted
- Date: 2026-10-19

## Context

Release archives carry a SHA256 sidecar and GitHub build provenance. Authenticode could additionally
show a verified publisher in Windows, but it requires a code-signing certificate, identity or business
verification, and a certificate lifecycle. The project does not have a certificate and does not plan
to obtain one.

## Decision

Brezza releases are distributed unsigned. The packager does not accept or invoke a signing
certificate, and the release workflow has no signing secrets or signing step. Integrity is provided
by the SHA256 sidecar and GitHub build provenance.

## Consequences

- Windows may show an unknown-publisher or SmartScreen warning when the executable is first
  downloaded. This is accepted for the project's distribution model.
- The release workflow cannot accidentally ship a signature that does not match the checksum or the
  provenance.
- If a certificate is obtained later, signing must be added as a new decision and must happen before
  the ZIP and checksum are produced.
