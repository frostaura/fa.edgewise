using Edgewise.Domain.Engines.Coach;
using Shouldly;

namespace Edgewise.Domain.Tests.Llm;

public class InsightValidatorTests
{
    private static readonly string TradeRef = $"trade:{Guid.NewGuid()}";
    private static readonly string AdherenceRef = $"adherence:{Guid.NewGuid()}";
    private static readonly IReadOnlySet<string> ValidIds =
        new HashSet<string> { TradeRef, AdherenceRef, "stat:history_digest", "bars:BTCUSDT:202607010000-202607030000" };

    // ------------------------------------------------------- citation format

    [Theory]
    [InlineData("stat:history_digest")]
    [InlineData("bars:BTCUSDT:202607010000-202607030000")]
    [InlineData("stat:expectancy_breakout")]
    public void Valid_citation_formats_are_accepted(string reference) =>
        InsightValidator.IsValidCitationFormat(reference).ShouldBeTrue(reference);

    [Fact]
    public void Guid_citation_formats_are_accepted()
    {
        InsightValidator.IsValidCitationFormat(TradeRef).ShouldBeTrue();
        InsightValidator.IsValidCitationFormat($"plan:{Guid.NewGuid()}").ShouldBeTrue();
        InsightValidator.IsValidCitationFormat($"fill:{Guid.NewGuid()}").ShouldBeTrue();
        InsightValidator.IsValidCitationFormat(AdherenceRef).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("trade:not-a-guid")]
    [InlineData("journal:whatever")]
    [InlineData("stat:")]
    [InlineData("bars:BTCUSDT")]
    [InlineData("random text")]
    public void Invalid_citation_formats_are_rejected(string reference) =>
        InsightValidator.IsValidCitationFormat(reference).ShouldBeFalse(reference);

    // -------------------------------------------------------- citation drops

    [Fact]
    public void Items_without_valid_citations_are_dropped()
    {
        var json = $$"""
        {
          "observations": [
            {"text": "Grounded claim about your exit", "citations": ["{{TradeRef}}"]},
            {"text": "Hallucinated claim with no citation", "citations": []},
            {"text": "Claim citing an unknown id", "citations": ["trade:{{Guid.NewGuid()}}"]},
            {"text": "Claim citing garbage", "citations": ["not-a-ref"]}
          ]
        }
        """;

        var result = InsightValidator.Validate(json, ValidIds);

        result.Success.ShouldBeTrue();
        result.Content!.Observations.Count.ShouldBe(1);
        result.Content.Observations[0].Text.ShouldBe("Grounded claim about your exit");
        result.Content.Observations[0].Citations.ShouldBe([TradeRef]);
    }

    [Fact]
    public void All_items_dropped_fails_validation_with_actionable_error()
    {
        var json = """{"observations":[{"text":"No citations here","citations":[]}]}""";

        var result = InsightValidator.Validate(json, ValidIds);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cite"));
    }

    [Fact]
    public void Duplicate_and_padded_citations_are_normalised()
    {
        var json = $$"""
        {"observations":[{"text":"One claim","citations":["{{TradeRef}}"," {{TradeRef}} ","{{AdherenceRef}}"]}]}
        """;

        var result = InsightValidator.Validate(json, ValidIds);

        result.Success.ShouldBeTrue();
        result.Content!.Observations[0].Citations.ShouldBe([TradeRef, AdherenceRef]);
    }

    // -------------------------------------------------------- directive gate

    [Fact]
    public void Directive_item_fails_validation_and_reports_the_text()
    {
        var json = $$"""
        {
          "observations": [{"text": "You exited before your rule fired", "citations": ["{{TradeRef}}"]}],
          "question": {"text": "You should sell now", "citations": ["{{TradeRef}}"]}
        }
        """;

        var result = InsightValidator.Validate(json, ValidIds);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("You should sell now"));
    }

    // -------------------------------------------------------- schema / parse

    [Fact]
    public void Malformed_json_fails()
    {
        var result = InsightValidator.Validate("{not json", ValidIds);
        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("invalid JSON"));
    }

    [Fact]
    public void Item_missing_text_fails()
    {
        var json = $$"""{"observations":[{"citations":["{{TradeRef}}"]}]}""";
        var result = InsightValidator.Validate(json, ValidIds);
        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("text"));
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated()
    {
        var json = $$"""
        {"observations":[{"text":"Fine","citations":["{{TradeRef}}"]}],"code":"DISPOSITION"}
        """;
        InsightValidator.Validate(json, ValidIds).Success.ShouldBeTrue();
    }

    // -------------------------------------------------------------- word cap

    [Fact]
    public void Word_cap_truncates_extra_items_keeping_order()
    {
        var sixtyWords = string.Join(' ', Enumerable.Repeat("word", 60));
        var content = new InsightContent
        {
            Observations =
            [
                new InsightItem(sixtyWords, [TradeRef]),
                new InsightItem(sixtyWords, [TradeRef]),
                new InsightItem(sixtyWords, [TradeRef]),
            ],
            Question = new InsightItem("Short question?", [TradeRef]),
        };

        var capped = InsightValidator.ApplyWordCap(content, 150);

        capped.Observations.Count.ShouldBe(2); // 3rd 60-word item would exceed 150
        capped.Question.ShouldNotBeNull();     // 120 + 2 still fits
        capped.AllItems().Sum(i => InsightValidator.CountWords(i.Text)).ShouldBeLessThanOrEqualTo(150);
    }

    [Fact]
    public void Word_cap_always_keeps_the_first_item_even_if_oversized()
    {
        var twoHundredWords = string.Join(' ', Enumerable.Repeat("w", 200));
        var content = new InsightContent { Observations = [new InsightItem(twoHundredWords, [TradeRef])] };

        var capped = InsightValidator.ApplyWordCap(content, 150);

        capped.Observations.Count.ShouldBe(1);
    }

    [Fact]
    public void Validate_applies_word_cap_end_to_end()
    {
        var big = string.Join(' ', Enumerable.Repeat("word", 100));
        var json = $$"""
        {
          "observations": [
            {"text": "{{big}}", "citations": ["{{TradeRef}}"]},
            {"text": "{{big}}", "citations": ["{{TradeRef}}"]}
          ]
        }
        """;

        var result = InsightValidator.Validate(json, ValidIds);

        result.Success.ShouldBeTrue();
        result.Content!.Observations.Count.ShouldBe(1);
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public void Complete_valid_insight_passes_with_all_sections()
    {
        var json = $$"""
        {
          "observations": [{"text": "The trade closed at 1.2R with grade A adherence", "citations": ["{{TradeRef}}", "{{AdherenceRef}}"]}],
          "deviations": [{"text": "The stop was widened after entry", "citations": ["{{AdherenceRef}}"]}],
          "riskFlags": [{"text": "Size ran above the risk profile", "citations": ["{{AdherenceRef}}"]}],
          "patternLinks": [{"text": "Third early exit in 30 days", "citations": ["stat:history_digest"]}],
          "question": {"text": "What did you feel at the exit moment?", "citations": ["{{TradeRef}}"]},
          "kudos": {"text": "A rule-following loss is good process", "citations": ["{{AdherenceRef}}"]}
        }
        """;

        var result = InsightValidator.Validate(json, ValidIds);

        result.Success.ShouldBeTrue();
        result.Content!.Observations.Count.ShouldBe(1);
        result.Content.Deviations.Count.ShouldBe(1);
        result.Content.RiskFlags.Count.ShouldBe(1);
        result.Content.PatternLinks.Count.ShouldBe(1);
        result.Content.Question.ShouldNotBeNull();
        result.Content.Kudos.ShouldNotBeNull();
    }
}
