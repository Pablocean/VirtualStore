# ADR 0005 — In-memory cache with explicit limits

- Status: Accepted
- Date: 2026-09-20 (waves 1–2)

## Context

OTP codes/attempts and hot reads need caching without operating Redis for a single-instance shop API.

## Decision

Use `IMemoryCache` behind `ICacheService`: absolute expiration when the caller passes one, otherwise 5-minute sliding. Current keys: `otp_{email}` (10 min), `otp_attempts_{email}` (10 min, lockout at 5). No distributed cache, no stampede protection, no persistence.

## Consequences

- (+) Zero ops burden; correct for single-instance deploys (compose, single container).
- (−) **Multi-instance deployments break OTP** (codes live on one node) — scaling out requires sticky sessions or a distributed cache (Redis) migration; OTP keys are already prefixed/normalized for that move.
- (−) Process recycle drops all entries — OTP users must re-request; acceptable for 10-minute codes.
- Next step if traffic grows: Redis-backed `IDistributedCache` for OTP + product/catalog reads, keeping the `ICacheService` surface.
