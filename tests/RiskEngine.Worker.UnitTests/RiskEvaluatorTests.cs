using BuildingBlocks.Messaging.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RiskEngine.Worker.Risk;
using Xunit;

namespace RiskEngine.Worker.UnitTests;

public sealed class RiskEvaluatorTests
{
    [Fact]
    public void ShouldApproveValidOrder()
    {
        var evaluator = BuildEvaluator(maxOrderValue: 100000m, blockedSymbols: [], start: "09:00", end: "22:00");
        var order = BuildOrder(symbol: "PETR4", quantity: 100, price: 30);

        var result = evaluator.Evaluate(order, new DateTimeOffset(2026, 2, 19, 13, 0, 0, TimeSpan.Zero));

        result.Approved.Should().BeTrue();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void ShouldRejectBlockedSymbol()
    {
        var evaluator = BuildEvaluator(maxOrderValue: 100000m, blockedSymbols: ["PETR4"], start: "09:00", end: "22:00");
        var order = BuildOrder(symbol: "PETR4", quantity: 100, price: 30);

        var result = evaluator.Evaluate(order, new DateTimeOffset(2026, 2, 19, 13, 0, 0, TimeSpan.Zero));

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("blocked");
    }

    [Fact]
    public void ShouldRejectOrderAboveMaxValue()
    {
        var evaluator = BuildEvaluator(maxOrderValue: 1000m, blockedSymbols: [], start: "09:00", end: "22:00");
        var order = BuildOrder(symbol: "VALE3", quantity: 100, price: 30);

        var result = evaluator.Evaluate(order, new DateTimeOffset(2026, 2, 19, 13, 0, 0, TimeSpan.Zero));

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("above max allowed");
    }

    [Fact]
    public void ShouldRejectOrderOutsideTradingWindow()
    {
        var evaluator = BuildEvaluator(maxOrderValue: 100000m, blockedSymbols: [], start: "09:00", end: "17:00");
        var order = BuildOrder(symbol: "VALE3", quantity: 10, price: 30);

        var result = evaluator.Evaluate(order, new DateTimeOffset(2026, 2, 19, 20, 0, 0, TimeSpan.Zero));

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("outside risk trading window");
    }

    private static RiskEvaluator BuildEvaluator(decimal maxOrderValue, string[] blockedSymbols, string start, string end)
    {
        var options = Options.Create(new RiskRulesOptions
        {
            MaxOrderValue = maxOrderValue,
            BlockedSymbols = blockedSymbols,
            TradingWindowStartUtc = start,
            TradingWindowEndUtc = end
        });

        return new RiskEvaluator(options);
    }

    private static OrderCreatedV1 BuildOrder(string symbol, decimal quantity, decimal price)
    {
        return new OrderCreatedV1(
            Guid.NewGuid(),
            Guid.NewGuid(),
            symbol,
            "BUY",
            quantity,
            price,
            DateTimeOffset.UtcNow,
            "PENDING");
    }
}
