using BuildingBlocks.Messaging.Contracts;
using Microsoft.Extensions.Options;

namespace RiskEngine.Worker.Risk;

public sealed class RiskEvaluator
{
    private readonly RiskRulesOptions _options;

    public RiskEvaluator(IOptions<RiskRulesOptions> options)
    {
        _options = options.Value;
    }

    public RiskEvaluationResult Evaluate(OrderCreatedV1 order, DateTimeOffset utcNow)
    {
        var blockedSymbols = _options.BlockedSymbols.Select(static x => x.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (blockedSymbols.Contains(order.Symbol.Trim().ToUpperInvariant()))
        {
            return RiskEvaluationResult.Reject($"Symbol {order.Symbol} is blocked by risk policy.");
        }

        var notional = order.Quantity * order.Price;
        if (notional > _options.MaxOrderValue)
        {
            return RiskEvaluationResult.Reject($"Order notional {notional} is above max allowed {_options.MaxOrderValue}.");
        }

        if (!TimeSpan.TryParse(_options.TradingWindowStartUtc, out var start))
        {
            start = TimeSpan.FromHours(9);
        }

        if (!TimeSpan.TryParse(_options.TradingWindowEndUtc, out var end))
        {
            end = TimeSpan.FromHours(22);
        }

        var current = utcNow.TimeOfDay;
        if (current < start || current > end)
        {
            return RiskEvaluationResult.Reject($"Order outside risk trading window [{start}-{end}] UTC.");
        }

        return RiskEvaluationResult.Pass();
    }
}
