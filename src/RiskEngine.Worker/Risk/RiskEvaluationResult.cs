namespace RiskEngine.Worker.Risk;

public sealed record RiskEvaluationResult(bool Approved, string? Reason)
{
    public static RiskEvaluationResult Pass() => new(true, null);

    public static RiskEvaluationResult Reject(string reason) => new(false, reason);
}
