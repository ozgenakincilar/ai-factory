# M11 — Verification Agent & GitHub Pull Request

Status: **M11.1 and M11.2 implemented** on 2026-10-17 (Factory-established verification checks and a
bounded correction loop). **M11.3** (GitHub branch and pull request) is ready to start once the user
has created the repository-scoped token; decisions: [Decisions](DECISIONS.md#m11-github-target-credential-and-merge-policy-2026-10-17-user-decision).

## M11.1 — verified results versus agent claims

When the Developer Agent finishes, the Factory runs the offline verification in a fresh sandbox and
evaluates the change itself (`ChangeChecks`, Factory.Core). The agent's finish summary is stored as its
**claim** only. Blocking checks:

- **sandbox**: every offline step passed (exit codes from the Docker daemon).
- **required_tests**: `dotnet test` writes a TRX report; a Factory step (`test-report`) extracts its
  counters and per-test outcomes right after the tests. No failed test, and the three protected
  architecture tests must appear by name as Passed. A run that exits 0 without running them (for
  example by skipping tests) is not verified.
- **layers**: a text scan of the authoritative sources, without executing anything: the domain
  references no `Microsoft.`, `Npgsql`, `System.Data` or other layer; the application no EF Core, Npgsql,
  ASP.NET Core, Infrastructure or Api; infrastructure not ASP.NET Core or the Api. Comment lines are ignored.
- **protected_files**: solution, projects, build settings, package manifests, Git files and the
  architecture tests are identical to the generated base (a second line of defence behind the workspace rules).

Advisory checks: **requirement_coverage** (every approved requirement referenced by a change) and
**new_tests** (the change adds or updates tests). The checks, their details and the number of
corrections are stored in the development evidence (additive fields of format 1) and shown on the
development page as "Factory checks", separate from the agent's log.

## M11.2 — correction loop

If the verdict is not verified and turns remain, the blocking findings and the failing step's output go
back to the agent as an observation ("NOT VERIFIED (correction n of 2)") and the loop continues; after two
corrections, or without turns left, the job ends as `VerificationFailed` with `check_<name>`. Every
correction runs under the same job cost cap; nothing is retried outside these bounds.

## Executed verification — 2026-10-17

1. Unit tests: TRX parsing; layer violations found in sources but not in comments or non-C# files; a
   green sandbox without the architecture tests (or with them failed, or without a report) is not
   verified; layer violations block while coverage and missing tests are warnings; a failed verification
   is returned to the agent, which corrects and is verified (1 correction); repeated failures end after 2
   corrections with `check_sandbox`; the claim is recorded, not trusted.
2. Docker: the scripted developer run is verified on real builds with 7 offline steps, the report
   showing 8 of 8 tests including the architecture tests; the verification-job and isolation cases pass
   with the added `test-report` step.
3. Web unit tests: 23 passed (Factory checks table and correction count on the development page).
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **992 passed,
   0 failed, 1 skipped** (the paid live test). TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-17_00_53_28.trx`, SHA-256 `ee4328bd3cc498e137b086fa92697a5e64064ae0fe3f4a089ee0bf11330e97a9`. No model calls.

## Limits

- The test report is written inside the sandbox, where the untrusted code also runs; a deliberately
  malicious workspace could still forge it. The checks raise the bar and are recorded; the sandbox
  limits the harm; human review remains required.
- The layer scan is textual (namespaces and `using` lines), not a semantic analysis; the reflection-based
  architecture tests remain the primary guard.
- No dependency vulnerability scan beyond the protected, already audited template manifests (the agent
  cannot change package references).
