using System.Text.Json;
using System.Text.RegularExpressions;

namespace Edgewise.Domain.Engines.Coach;

/// <summary>
/// Pure, deterministic validation pipeline for coach insights — the behavioural contract
/// enforced in code rather than prompts:
///  (1) grounded-only — every item must cite at least one supplied context id; uncited items are dropped;
///  (2) no directives — text that instructs market action is rejected;
///  (3) total rendered text is capped at <see cref="MaxWords"/> words (extra items truncated, order kept).
/// The formal JSON-Schema check lives in Infrastructure (JsonSchema.Net); everything here is
/// framework-free so it can be unit-tested from Edgewise.Domain.Tests.
/// </summary>
public static partial class InsightValidator
{
    public const int MaxWords = 150;

    // ------------------------------------------------------------ citations

    [GeneratedRegex(
        @"^(trade|plan|fill|adherence):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$|^bars:[A-Za-z0-9._\-]+:[A-Za-z0-9._\-]+$|^stat:[A-Za-z0-9._\-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CitationFormatRegex();

    /// <summary>True when the string is one of the recognised citation ref shapes
    /// ("trade:{guid}", "plan:{guid}", "fill:{guid}", "adherence:{guid}", "bars:{instrument}:{range}", "stat:{key}").</summary>
    public static bool IsValidCitationFormat(string reference) =>
        !string.IsNullOrWhiteSpace(reference) && CitationFormatRegex().IsMatch(reference.Trim());

    // -------------------------------------------------------- directive gate

    // Advice phrasing anywhere in the text ("you should", "you must", "I recommend",
    // "consider buying/selling") plus bare buy/sell as market verbs.
    [GeneratedRegex(
        @"\b(you\s+should|you\s+must|i\s+recommend|consider\s+(buying|selling)|buy|sell)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdviceRegex();

    // Standalone imperatives at the start of the text.
    [GeneratedRegex(
        @"^\s*(buy|sell|enter|exit|add|close|take\s+profit|cut)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImperativeRegex();

    /// <summary>
    /// True when the text reads as a market directive ("You should sell now", "Buy the dip")
    /// rather than process coaching ("You exited before your rule fired").
    /// </summary>
    public static bool IsDirective(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return AdviceRegex().IsMatch(text) || ImperativeRegex().IsMatch(text);
    }

    // -------------------------------------------------------------- word cap

    public static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Caps total rendered words at <paramref name="maxWords"/> by dropping whole items past the
    /// budget, walking sections in render order (observations → deviations → riskFlags →
    /// patternLinks → question → kudos). The first item is always kept.
    /// </summary>
    public static InsightContent ApplyWordCap(InsightContent content, int maxWords = MaxWords)
    {
        var used = 0;
        var any = false;

        List<InsightItem> Take(IReadOnlyList<InsightItem> items)
        {
            var kept = new List<InsightItem>();
            foreach (var item in items)
            {
                var words = CountWords(item.Text);
                if (any && used + words > maxWords)
                {
                    continue;
                }

                kept.Add(item);
                used += words;
                any = true;
            }

            return kept;
        }

        InsightItem? TakeOne(InsightItem? item)
        {
            if (item is null)
            {
                return null;
            }

            var words = CountWords(item.Text);
            if (any && used + words > maxWords)
            {
                return null;
            }

            used += words;
            any = true;
            return item;
        }

        return new InsightContent
        {
            Observations = Take(content.Observations),
            Deviations = Take(content.Deviations),
            RiskFlags = Take(content.RiskFlags),
            PatternLinks = Take(content.PatternLinks),
            Question = TakeOne(content.Question),
            Kudos = TakeOne(content.Kudos),
        };
    }

    // ---------------------------------------------------------------- parse

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ItemDto
    {
        public string? Text { get; set; }
        public List<string>? Citations { get; set; }
    }

    private sealed class ContentDto
    {
        public List<ItemDto>? Observations { get; set; }
        public List<ItemDto>? Deviations { get; set; }
        public List<ItemDto>? RiskFlags { get; set; }
        public List<ItemDto>? PatternLinks { get; set; }
        public ItemDto? Question { get; set; }
        public ItemDto? Kudos { get; set; }
    }

    /// <summary>Parses insight JSON into the canonical model. Returns null (with errors) on malformed input.</summary>
    public static InsightContent? Parse(string json, out IReadOnlyList<string> errors)
    {
        var errorList = new List<string>();
        errors = errorList;

        ContentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ContentDto>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            errorList.Add($"invalid JSON: {ex.Message}");
            return null;
        }

        if (dto is null)
        {
            errorList.Add("invalid JSON: document is null");
            return null;
        }

        InsightItem? MapItem(ItemDto? item, string where)
        {
            if (item is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                errorList.Add($"{where}: item is missing 'text'");
                return null;
            }

            return new InsightItem(item.Text.Trim(), item.Citations ?? []);
        }

        List<InsightItem> MapList(List<ItemDto>? items, string where) =>
            items is null
                ? []
                : [.. items.Select(i => MapItem(i, where)).Where(i => i is not null).Cast<InsightItem>()];

        var content = new InsightContent
        {
            Observations = MapList(dto.Observations, "observations"),
            Deviations = MapList(dto.Deviations, "deviations"),
            RiskFlags = MapList(dto.RiskFlags, "riskFlags"),
            PatternLinks = MapList(dto.PatternLinks, "patternLinks"),
            Question = MapItem(dto.Question, "question"),
            Kudos = MapItem(dto.Kudos, "kudos"),
        };

        return errorList.Count > 0 ? null : content;
    }

    // ------------------------------------------------------------- pipeline

    /// <summary>
    /// Full pure pipeline: parse → drop uncited items → reject directives → word cap.
    /// A failed result carries actionable errors intended to be appended to the retry prompt.
    /// </summary>
    public static InsightValidationResult Validate(string json, IReadOnlySet<string> validCitationIds)
    {
        var content = Parse(json, out var parseErrors);
        if (content is null)
        {
            return InsightValidationResult.Fail([.. parseErrors]);
        }

        return Sanitize(content, validCitationIds);
    }

    /// <summary>Citation check + directive gate + word cap over an already-parsed content model.</summary>
    public static InsightValidationResult Sanitize(InsightContent content, IReadOnlySet<string> validCitationIds)
    {
        var errors = new List<string>();

        InsightItem? Check(InsightItem? item)
        {
            if (item is null)
            {
                return null;
            }

            if (IsDirective(item.Text))
            {
                errors.Add(
                    $"directive language is forbidden (never instruct market action): \"{item.Text}\"");
                return null;
            }

            var cited = item.Citations
                .Where(c => IsValidCitationFormat(c) && validCitationIds.Contains(c.Trim()))
                .Select(c => c.Trim())
                .Distinct()
                .ToList();
            // Grounded-only: items with zero valid citations are silently dropped.
            return cited.Count == 0 ? null : item with { Citations = cited };
        }

        List<InsightItem> CheckList(IReadOnlyList<InsightItem> items) =>
            [.. items.Select(Check).Where(i => i is not null).Cast<InsightItem>()];

        var sanitized = new InsightContent
        {
            Observations = CheckList(content.Observations),
            Deviations = CheckList(content.Deviations),
            RiskFlags = CheckList(content.RiskFlags),
            PatternLinks = CheckList(content.PatternLinks),
            Question = Check(content.Question),
            Kudos = Check(content.Kudos),
        };

        if (errors.Count > 0)
        {
            return InsightValidationResult.Fail([.. errors]);
        }

        if (sanitized.IsEmpty)
        {
            return InsightValidationResult.Fail(
                "every item was dropped: cite the supplied context ids (e.g. \"trade:{id}\", \"stat:{key}\") on each item");
        }

        return InsightValidationResult.Ok(ApplyWordCap(sanitized));
    }
}
