using Edgewise.Domain.Engines.Coach;
using Shouldly;

namespace Edgewise.Domain.Tests.Llm;

/// <summary>
/// Corpus tests for the directive gate: coaching language about past process must pass,
/// anything instructing market action must be rejected.
/// </summary>
public class DirectiveGateCorpusTests
{
    [Theory]
    // Explicit advice phrasing.
    [InlineData("You should sell now")]
    [InlineData("You should add to this position")]
    [InlineData("You must cut this position immediately")]
    [InlineData("You must exit before earnings")]
    [InlineData("I recommend exiting")]
    [InlineData("I recommend a tighter stop on the next entry")]
    [InlineData("Consider buying more at support")]
    [InlineData("Consider selling into this rally")]
    // Bare buy/sell as market verbs.
    [InlineData("Buy the dip here")]
    [InlineData("It is time to sell and take your profit")]
    [InlineData("The smart move is to buy more here")]
    // Standalone imperatives.
    [InlineData("Sell half your position before the close")]
    [InlineData("Enter on the retest of the breakout level")]
    [InlineData("Exit now before the news hits")]
    [InlineData("Add to your winners on strength")]
    [InlineData("Close the trade at the open tomorrow")]
    [InlineData("Take profit at the prior high")]
    public void Directive_sentences_are_rejected(string text) =>
        InsightValidator.IsDirective(text).ShouldBeTrue(text);

    [Theory]
    [InlineData("You exited before your rule fired")]
    [InlineData("Your size was 2× profile on a red day")]
    [InlineData("This was your third early exit in 30 days on trend setups")]
    [InlineData("Your stop was widened after entry, which broke the plan")]
    [InlineData("The entry chased 1.4% beyond your trigger price")]
    [InlineData("Adherence held at grade A despite the loss")]
    [InlineData("Winners were held 2.1× longer than losers this month")]
    [InlineData("Your plan committed to a 2R target and the exit came at 0.6R")]
    [InlineData("Three entries this week came within 30 minutes of a prior loss")]
    [InlineData("The journal note mentions FOMO before the entry fill")]
    [InlineData("Expectancy on breakout setups is 0.42R over 38 trades")]
    [InlineData("This trade followed the plan from trigger to target")]
    [InlineData("The position was opened without an active plan")]
    [InlineData("Losses after 22:00 account for most of the week's drawdown")]
    [InlineData("Risk stayed inside the 1% profile on every fill")]
    [InlineData("The stop distance implied 1.8% account risk versus the 1% budget")]
    [InlineData("Both exit fills matched the ladder rule in the plan")]
    [InlineData("Tilt signature: expectancy drops 0.5R within an hour of a loss")]
    [InlineData("A calm emotion tag preceded your best trades this quarter")]
    [InlineData("The trade was closed manually two minutes before the stop level")]
    [InlineData("Process quality was strong even though the outcome was a loss")]
    public void Coaching_sentences_pass(string text) =>
        InsightValidator.IsDirective(text).ShouldBeFalse(text);

    [Fact]
    public void Empty_and_whitespace_are_not_directives()
    {
        InsightValidator.IsDirective("").ShouldBeFalse();
        InsightValidator.IsDirective("   ").ShouldBeFalse();
    }

    [Fact]
    public void Gate_is_case_insensitive()
    {
        InsightValidator.IsDirective("YOU SHOULD SELL NOW").ShouldBeTrue();
        InsightValidator.IsDirective("buy the breakout").ShouldBeTrue();
    }
}
