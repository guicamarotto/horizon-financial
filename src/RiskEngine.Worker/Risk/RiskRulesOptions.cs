namespace RiskEngine.Worker.Risk;

public sealed class RiskRulesOptions
{
    public const string SectionName = "RiskRules";

    public decimal MaxOrderValue { get; set; } = 100000m;
    public string[] BlockedSymbols { get; set; } = [];
    public string TradingWindowStartUtc { get; set; } = "09:00";
    public string TradingWindowEndUtc { get; set; } = "22:00";
}
