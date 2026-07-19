namespace Edgewise.Domain.Engines.Coach;

/// <summary>A single insight claim: rendered text plus the context-pack ids that ground it.</summary>
public sealed record InsightItem(string Text, IReadOnlyList<string> Citations);

/// <summary>
/// The canonical insight payload stored in <c>Insight.ContentJson</c> and rendered by the UI.
/// All sections are optional; items with no valid citations are dropped by the validator.
/// </summary>
public sealed record InsightContent
{
    public IReadOnlyList<InsightItem> Observations { get; init; } = [];
    public IReadOnlyList<InsightItem> Deviations { get; init; } = [];
    public IReadOnlyList<InsightItem> RiskFlags { get; init; } = [];
    public IReadOnlyList<InsightItem> PatternLinks { get; init; } = [];
    public InsightItem? Question { get; init; }
    public InsightItem? Kudos { get; init; }

    public bool IsEmpty =>
        Observations.Count == 0 && Deviations.Count == 0 && RiskFlags.Count == 0 &&
        PatternLinks.Count == 0 && Question is null && Kudos is null;

    public IEnumerable<InsightItem> AllItems()
    {
        foreach (var item in Observations) yield return item;
        foreach (var item in Deviations) yield return item;
        foreach (var item in RiskFlags) yield return item;
        foreach (var item in PatternLinks) yield return item;
        if (Question is not null) yield return Question;
        if (Kudos is not null) yield return Kudos;
    }
}

/// <summary>Outcome of validating/sanitising LLM-produced insight JSON.</summary>
public sealed record InsightValidationResult(
    bool Success,
    InsightContent? Content,
    IReadOnlyList<string> Errors)
{
    public static InsightValidationResult Fail(params string[] errors) => new(false, null, errors);
    public static InsightValidationResult Ok(InsightContent content) => new(true, content, []);
}
