using System;
using System.Collections.Generic;
using System.Linq;

namespace VNotch.Services.ClaudeCode;

internal sealed class ClaudeQuestionAnswers(IReadOnlyList<ClaudeQuestion> questions)
{
    private readonly Dictionary<string, List<string>> _picks = new(StringComparer.Ordinal);
    private bool _submitted;

    internal bool IsPicked(ClaudeQuestion question, string label) =>
        _picks.TryGetValue(question.Text, out var chosen) && chosen.Contains(label);

    internal bool HasPick(ClaudeQuestion question) =>
        _picks.TryGetValue(question.Text, out var chosen) && chosen.Count > 0;

    internal bool AllAnswered => questions.All(HasPick);

    internal void Pick(ClaudeQuestion question, string label)
    {
        if (!_picks.TryGetValue(question.Text, out var chosen)) _picks[question.Text] = chosen = new();
        if (!question.MultiSelect) chosen.Clear();
        if (!chosen.Remove(label)) chosen.Add(label);
    }

    internal string LabelsFor(ClaudeQuestion question) =>
        string.Join(", ", question.Options.Where(o => IsPicked(question, o.Label)).Select(o => o.Label));

    internal bool TrySubmit(Func<IReadOnlyDictionary<string, string>, bool> answer)
    {
        if (_submitted || !AllAnswered) return false;
        _submitted = true;
        var answers = new Dictionary<string, string>(questions.Count, StringComparer.Ordinal);
        foreach (var question in questions) answers[question.Text] = LabelsFor(question);
        return answer(answers);
    }
}
