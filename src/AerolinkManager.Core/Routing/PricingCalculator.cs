using AerolinkManager.Core.Models;

namespace AerolinkManager.Core.Routing;

/// <summary>Estimated-cost math from configured <see cref="ModelPricing"/>. No pricing → cost unknown (null).</summary>
public static class PricingCalculator
{
    private const decimal PerMillion = 1_000_000m;

    public static ModelPricing? Find(IEnumerable<ModelPricing> pricing, string providerId, string? modelValue)
    {
        if (string.IsNullOrWhiteSpace(modelValue))
        {
            return null;
        }

        return pricing.FirstOrDefault(p => p.Enabled
            && string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.ModelValue, modelValue, StringComparison.OrdinalIgnoreCase));
    }

    public static decimal? EstimateCost(
        ModelPricing? pricing,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens = 0,
        long cacheWriteTokens = 0)
    {
        if (pricing is null)
        {
            return null;
        }

        return (inputTokens * pricing.InputPerMillion
            + outputTokens * pricing.OutputPerMillion
            + cacheReadTokens * pricing.CacheReadPerMillion
            + cacheWriteTokens * pricing.CacheWritePerMillion) / PerMillion;
    }

    /// <summary>
    /// Blended input+output per-million rate used to order candidates by price when
    /// no token estimate is supplied. Missing pricing sorts last (worst) on purpose.
    /// </summary>
    public static decimal BlendedRatePerMillion(ModelPricing? pricing) =>
        pricing is null ? decimal.MaxValue : pricing.InputPerMillion + pricing.OutputPerMillion;
}
