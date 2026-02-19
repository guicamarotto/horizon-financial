namespace LimitService.Worker.Limits;

public sealed class LimitRulesOptions
{
    public const string SectionName = "LimitRules";

    public decimal AccountMaxLimit { get; set; } = 250000m;
    public string[] FailSymbols { get; set; } = [];
}
