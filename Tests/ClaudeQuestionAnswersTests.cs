using System.Text.Json.Nodes;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeQuestionAnswersTests
{
    private static ClaudeQuestion Question(string text, bool multiSelect = false) =>
        new(text, "", [new("First", null), new("Second", null), new("Third", null)], multiSelect);

    [Fact]
    public void SingleChoiceReplacesThePreviousPick()
    {
        var question = Question("Which one?");
        var picks = new ClaudeQuestionAnswers([question]);
        picks.Pick(question, "First");
        picks.Pick(question, "Second");

        Assert.False(picks.IsPicked(question, "First"));
        Assert.True(picks.TrySubmit(answers =>
        {
            Assert.Equal("Second", answers[question.Text]);
            return true;
        }));
    }

    [Fact]
    public void MultiSelectTogglesAndJoinsLabelsInOptionOrder()
    {
        var question = Question("Which ones?", multiSelect: true);
        var picks = new ClaudeQuestionAnswers([question]);
        picks.Pick(question, "Third");
        picks.Pick(question, "First");
        picks.Pick(question, "Second");
        picks.Pick(question, "Second");

        Assert.True(picks.TrySubmit(answers =>
        {
            Assert.Equal("First, Third", answers[question.Text]);
            return true;
        }));
    }

    [Fact]
    public void SeveralQuestionsSubmitTogetherAfterTheLastPick()
    {
        var first = Question("Where?");
        var second = Question("When?");
        var picks = new ClaudeQuestionAnswers([first, second]);
        picks.Pick(first, "Third");
        Assert.False(picks.TrySubmit(_ => throw new InvalidOperationException("The second answer is still missing.")));
        picks.Pick(second, "First");

        Assert.True(picks.TrySubmit(answers =>
        {
            Assert.Equal(2, answers.Count);
            Assert.Equal("Third", answers[first.Text]);
            Assert.Equal("First", answers[second.Text]);
            return true;
        }));
        Assert.False(picks.TrySubmit(_ => throw new InvalidOperationException("Already submitted.")));
    }

    [Fact]
    public async Task AnAnswerFromElsewhereWinsAndTheStaleSubmissionIsNotRetried()
    {
        var question = Question("Which one?");
        var tracker = new ClaudeSessionTracker();
        var response = tracker.HandleHookAsync(ClaudeAskPillTests.AskQuestions(question));
        var request = Assert.Single(tracker.GetPending());
        var picks = new ClaudeQuestionAnswers(request.Questions!);
        picks.Pick(request.Questions![0], "First");

        Assert.True(tracker.Answer(request.Id, new Dictionary<string, string> { [question.Text] = "Third" }));
        Assert.False(picks.TrySubmit(answers => tracker.Answer(request.Id, answers)));
        Assert.False(picks.TrySubmit(_ => throw new InvalidOperationException("Stale answers must not be retried.")));
        var values = JsonNode.Parse(await response)!["hookSpecificOutput"]!["decision"]!["updatedInput"]!["answers"];
        Assert.Equal("Third", (string?)values![question.Text]);
    }
}
