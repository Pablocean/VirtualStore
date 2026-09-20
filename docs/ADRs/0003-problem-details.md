# ADR 0003 — RFC 7807 ProblemDetails via IExceptionHandler

- Status: Accepted
- Date: 2026-09-20 (waves 1–2)

## Context

Errors were previously ad-hoc (`BadRequest(new { message })` in places, raw exceptions elsewhere). Clients need a uniform, correlatable error shape.

## Decision

Centralize on `ApiExceptionHandler : IExceptionHandler` (registered with `AddProblemDetails` + `AddExceptionHandler`, active via `UseExceptionHandler`): `ValidationException` → 400 + `errors` map; `UnauthorizedAccessException` → 401; `KeyNotFoundException` → 404; `InvalidOperationException` → 409; fallback → 500 (message only in Development). Every body carries `traceId` (`Activity.Current.Id ?? HttpContext.TraceIdentifier`).

## Consequences

- (+) One contract for all 32 routes; `traceId` joins logs to client reports.
- (+) Services stay HTTP-agnostic — they throw domain exceptions, the API layer translates.
- (−) Some legacy `BadRequest(new { message })` shapes remain (auth cookie-missing, webhook signature) — they bypass the envelope; harmonize in a later wave.
- (−) `429` is reserved but unmapped until wave 2f rate limiting lands.
