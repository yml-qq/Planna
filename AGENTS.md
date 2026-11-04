# AGENTS.md — AI Coding Contract for Planna

> Copy this file into the root of the actual source repository. It is a mandatory engineering rulebook, not an instruction to generate the entire app in one shot.

## Project invariants

1. Product: Planna (普拉清单), a modern, lightweight, AI-native, offline-first, personal-first task/time assistant; no enterprise IM or large multi-tenant team suite.
2. Server: C# / ASP.NET Core on .NET 10.0 + PostgreSQL 18, maintainable modular monolith, separate .NET worker. PostgreSQL 16 compatibility is not required. Use EF Core for simple CRUD and Dapper for complex read queries; keep their data-access implementations separate rather than mixing them in one Repository.
3. Desktop: Tauri 2 + Vue 3 + TypeScript + Vite. SQLite and OS functionality through vetted Rust commands.
4. Android: native Kotlin + Jetpack Compose + Room. NEVER introduce Flutter/React Native/Tauri Mobile.
5. iOS: native Swift + SwiftUI + SQLite (GRDB candidate). NEVER introduce Flutter/React Native/Tauri Mobile.
6. Local SQLite is the UI single source of truth. A local mutation and its outbox operation must be written in one transaction.
7. Sync must use per-owner committed revision, stable mutation IDs, idempotent push, tombstones, cursor pull, version conflict handling. `updated_at` is NOT a sync cursor.
8. Temporal data must distinguish absolute instant, all-day LocalDate, and zoned recurring local time. RRULE does not represent after-completion recurrence.
9. AI NEVER accesses raw SQL, shell, arbitrary network or production credentials. Read tools have scoped authorization; bulk writes require persisted preview, revalidation, user confirmation and audit.
10. Authentication/authorization must enforce object owner and nested reference ownership at server-side. No user_id from request body may authorize access.
11. Data export and account deletion are integral requirements. AI opt-out must not break task CRUD or local notifications.
12. No real user data may be sent to an AI coding tool or a model without explicit lawful authorization.

## Before coding

- Inspect real repository files, current migrations, installed SDK versions, and git status.
- Read `00-README.md` and all relevant `docs/`, `spec/` files; if absent ask to add the baseline docs first.
- Define one bounded task with explicit in-scope, out-of-scope, acceptance and tests.
- When specifications conflict, present the discrepancy and propose a minimal ADR/docs fix; do not silently choose different behavior per platform.

## Change rules

- No surprise framework changes or drive-by refactors.
- Schema/API changes require migrations/OpenAPI/schema updates and contract tests.
- All writes need idempotency, version checks, authorization, and safe errors.
- Date/notification changes need TZ and DST test cases.
- Sync changes require crash/retry/duplicate/concurrency fault tests.
- AI changes require JSON schema, tool authorization, budget cap, prompt injection, stale-plan and provider-failure tests.
- Do not claim tests passed without actually running them. If environment lacks SDK or certificates, mark tests unverified precisely.

## Deliverable format

1. Summary of implementation and deliberate non-goals.
2. Files changed, migrations, and API contract changes.
3. Test/build/lint commands actually executed and observed results.
4. Security/offline/AI/notification risks and mitigations.
5. Remaining gaps and next suggested smallest task.

## Required CI preference

.NET server: dotnet format, build, test, EF Core migration/integration and Dapper query tests. Vue/Tauri: lint, typecheck, unit, Tauri Rust tests, platform smoke. Android: Gradle unit/UI and Room migrations. iOS: Xcode build, Swift unit/XCUITest on macOS. API schemas: OpenAPI lint + shared fixtures.
