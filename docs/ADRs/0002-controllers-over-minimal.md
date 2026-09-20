# ADR 0002 — Controllers over minimal APIs

- Status: Accepted
- Date: 2026-09-20 (waves 1–2)

## Context

Nine resource areas (Auth, Users, Products, Cart, Orders, EnterpriseInfo, Payments, StripeWebhook, Categories) need attribute routing, per-action auth policies, model validation, and `ProducesResponseType` metadata for the OpenAPI/Scalar doc.

## Decision

Use attribute-routed controllers (`[ApiController]`, `api/[controller]` or explicit routes) with `FluentValidation` auto-validation and explicit `[Authorize(Roles)]` / `[AllowAnonymous]` on every action. No minimal-API endpoints.

## Consequences

- (+) Auth matrix is grep-able per action; OpenAPI picks up response types for Scalar.
- (+) Filters/middleware (`ApiExceptionHandler`, status-code pages) apply uniformly; testing via `WebApplicationFactory` + `MapControllers` is standard.
- (−) More ceremony per endpoint than minimal APIs; mitigated by the module-slice recipe in `AGENTS.md`.
- Note: `CartController.UpdateItem` takes a raw `[FromBody] int` — kept for backward compatibility; changing it needs a versioning plan.
