# M9 — Application Templates & Secure Execution

Status: **M9.1 implemented** (starter template, registry, deterministic generation) on 2026-10-16
after the user approved Docker Desktop + WSL 2, the template stack and the DDD layer layout.
M9.2 (offline sandbox runner) and M9.3 (per-job workspaces, evidence, worker/UI integration) are
planned. This document describes inspected code and executed local verification, not production
readiness.

## Slices

| Slice | Scope | Status |
| --- | --- | --- |
| M9.1 | Versioned starter template, minimal registry, deterministic generator, container verification of generated output | Implemented |
| M9.2 | Digest-pinned runner image with an offline package cache; sandbox runner with network off, read-only root, non-root user, CPU/memory/PID/time/disk limits, no credentials; malicious-code tests | Planned |
| M9.3 | Per-job isolated workspaces, immutable hashed verification evidence, durable job integration in the worker, evidence in the Product Workspace, cross-tenant isolation tests | Planned |

## M9.1 — starter template

- **Template** `dotnet-angular-postgres-ddd` 1.0.0 in `templates/starter-v1/` (37 text files). It is a
  buildable solution under the placeholder name `FactoryStarter` (`factory-starter` for the web app):
  - `FactoryStarter.Domain`: no package or project references; `Entity`, `AggregateRoot` (domain
    events), `IDomainEvent`, `DomainException`.
  - `FactoryStarter.Application`: references Domain only (plus DI abstractions); `IUnitOfWork` port.
  - `FactoryStarter.Infrastructure`: EF Core + Npgsql `AppDbContext` implementing `IUnitOfWork`.
  - `FactoryStarter.Api`: Minimal API composition root with `/health`.
  - `tests/FactoryStarter.Tests`: architecture tests enforcing the dependency direction, domain and
    API tests (no database needed).
  - `web/`: Angular 22.2.3 (standalone, zoneless, Vitest) with an exact-version `package-lock.json`.
  - .NET packages use the Factory's own pinned versions; `TreatWarningsAsErrors` and deterministic builds are on.
- **Registry** `templates/registry.json`: id, version, directory, placeholders and a SHA-256 content
  hash. `FileSystemTemplateCatalog` refuses an unknown template, an invalid registry, links and any
  file change (hash mismatch). Build/package output directories are not template content.
- **Content hash**: SHA-256 over `path NUL content NUL` for every file in ordinal path order; paths are
  relative, `/`-separated and restricted to `[A-Za-z0-9._-]` segments; content is UTF-8 text without
  NUL, with LF line endings (`templates/.gitattributes` keeps LF on checkout; CRLF is normalized).
  Limits: 500 files, 4 MiB.
- **Generation** (`StarterGenerator`, pure domain code in Factory.Core): verifies the template hash,
  replaces the placeholders in paths and contents with an `ApplicationName` (PascalCase, 2–6 words,
  ASCII; kebab form derived) and returns the files with a manifest hash. The same template, version and
  name always give the same files and hash. Names containing the placeholder are refused.
  `GeneratedApplicationWriter` writes only into a new or empty directory and refuses paths that would
  leave it.

## Executed verification — 2026-10-16

Docker Desktop 29.8.2 on WSL 2; images pinned by digest:
`mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1…5ffa` (SDK 10.0.402) and
`node@sha256:a5f4366e…aee5` (Node 24.22.0).

1. The template itself in containers: `dotnet test` 6 passed; `npm ci`, `ng test` 1 passed,
   `ng build` 190 kB initial. The lock file was created with `npm install --package-lock-only
   --ignore-scripts`; `npm audit`: 0 vulnerabilities.
2. Negative architecture check: adding EF Core to the domain project and using a `DbContext` type made
   `Domain_DependsOnlyOnTheBaseClassLibrary` fail (exit 1). This shows the guard works; it is not a
   complete architecture analysis.
3. Factory tests: 27 template unit tests (determinism, placeholder replacement, names, tampering,
   unsafe paths, registry, writer) and one Docker test that generates `SampleShop` and runs
   `dotnet test`, `npm ci`, `ng test` and `ng build` in the pinned containers (2 min 11 s). The C#
   content hash equals the independently computed registry hash.
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **913 passed,
   0 failed, 0 skipped**, including four live Keycloak cases and the Docker case. TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_12_03_35.trx`, SHA-256
   `a0a29e660ad40e878640e8edba80988772f3ea8cb9a9c9767279c88638ef7209`.

## Limits

- M9.1 builds only Factory-authored template output, and package restore in those containers uses the
  network. Untrusted, agent-modified code must not be built this way; that is the M9.2 sandbox.
- No PostgreSQL integration test in the template yet (the health endpoint needs no database).
- One template and one stack; the registry is a file in the repository, not a database or service.
- Names with consecutive capitals (for example `APIGateway`) or non-ASCII letters are refused.
