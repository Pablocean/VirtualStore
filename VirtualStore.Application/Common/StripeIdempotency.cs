using System.Globalization;

namespace VirtualStore.Application.Common;

/// <summary>
/// Deterministic Stripe idempotency keys (Epic B / ADR-0007).
/// Stripe keeps idempotency keys for 24h; these caller-computed keys make retries
/// safe without persisting key state: the same (order, operation) always yields
/// the same key, so double-submits collapse into a single Stripe operation.
/// </summary>
public static class StripeIdempotency
{
    /// <summary>Payment-intent key for an order: <c>order:{orderId}:intent</c>.</summary>
    public static string IntentKey(string orderId) => $"order:{orderId}:intent";

    /// <summary>
    /// Refund key: <c>order:{orderId}:refund:{amount ?? "full"}</c>.
    /// Amount is formatted invariantly (<c>0.##</c>) so <c>10.50m</c> and
    /// <c>10.5m</c> map to the same key.
    /// </summary>
    public static string RefundKey(string orderId, decimal? amount) =>
        $"order:{orderId}:refund:{(amount.HasValue ? amount.Value.ToString("0.##", CultureInfo.InvariantCulture) : "full")}";
}
