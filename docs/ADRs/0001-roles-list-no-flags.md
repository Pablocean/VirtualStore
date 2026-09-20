# ADR 0001 — Roles as discrete list, not [Flags]

- Status: Accepted
- Date: 2026-09-20 (waves 1–2)

## Context

`User` needs multi-role membership (`Customer`, `Manager`, `Admin`). The enum values (1, 2, 4) look flag-compatible, and an early revision risked `[Flags]` + `HasFlag` checks.

## Decision

Model membership as `List<UserRole>` of discrete values. No `[Flags]` attribute, no bitwise operators anywhere. All validation funnels through `UserRoles.EnsureValid` (distinct + `Enum.IsDefined`, non-empty). Tokens carry one `role` claim per role; ASP.NET `IsInRole` / `[Authorize(Roles=...)]` match on those claims.

## Consequences

- (+) Impossible to smuggle combined bit values (e.g. `3`) into a role check; undefined integers are rejected at the boundary.
- (+) JWT `role` claims stay human-readable; seeder bootstrap (`Admin, Manager, Customer`) is explicit.
- (−) Slightly more storage than a bitmask; membership tests are linear scans — irrelevant at ≤ 3 roles.
- Constraint: contributors must never use `|`, `&`, `HasFlag` on `UserRole` (see `AGENTS.md`).
