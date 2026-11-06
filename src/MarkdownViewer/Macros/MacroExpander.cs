using System.Text.RegularExpressions;

namespace MarkdownViewer.Macros;

/// <summary>
/// Finds a macro trigger that the user has just finished typing and works out what it expands to.
/// </summary>
/// <remarks>
/// A trigger only counts when it starts a word (at the start of a line, or after a space,
/// tab or opening bracket), so "name@730" in an e-mail address is left alone.
/// </remarks>
public class MacroExpander
{
    /// <summary>The placeholder in triggers and replacements that stands for a number.</summary>
    public const string NumberPlaceholder = "{n}";

    /// <summary>Characters that end a macro trigger and make it expand (besides whitespace, which always does).</summary>
    public const string TerminatorCharacters = ".,;:!?)]";

    /// <summary>Name of the regex group that captures the typed number.</summary>
    private const string NumberGroupName = "number";

    /// <summary>A macro together with the regular expression built from its trigger.</summary>
    private sealed record CompiledMacro(MacroDefinition Definition, Regex TriggerPattern);

    /// <summary>The enabled macros, in the order the user listed them (the first match wins).</summary>
    private readonly List<CompiledMacro> compiledMacros = new();

    /// <summary>Prepares the given macros for fast matching. Disabled or empty ones are skipped.</summary>
    public MacroExpander(IEnumerable<MacroDefinition> macroDefinitions)
    {
        foreach (var macroDefinition in macroDefinitions)
        {
            if (!macroDefinition.IsEnabled || string.IsNullOrWhiteSpace(macroDefinition.Trigger))
            {
                continue;
            }

            compiledMacros.Add(new CompiledMacro(macroDefinition, BuildTriggerPattern(macroDefinition.Trigger)));
        }
    }

    /// <summary>True if there is at least one macro to look for.</summary>
    public bool HasMacros => compiledMacros.Count > 0;

    /// <summary>
    /// Turns a trigger such as "@{n}" into a regular expression that must match right at the end
    /// of the text, and must start at the beginning of a word.
    /// </summary>
    private static Regex BuildTriggerPattern(string triggerText)
    {
        // Split around {n}; the literal parts are escaped so characters like "." or "#" are typed as-is.
        string[] literalParts = triggerText.Split(NumberPlaceholder, StringSplitOptions.None);
        var patternBuilder = new System.Text.StringBuilder();

        // The trigger must start a word: nothing before it, or whitespace or an opening bracket.
        patternBuilder.Append(@"(?<![^\s(\[])");

        for (int partIndex = 0; partIndex < literalParts.Length; partIndex++)
        {
            if (partIndex > 0)
            {
                // The first {n} captures the number; any later {n} must repeat the same number.
                patternBuilder.Append(partIndex == 1 ? $@"(?<{NumberGroupName}>\d+)" : $@"\k<{NumberGroupName}>");
            }
            patternBuilder.Append(Regex.Escape(literalParts[partIndex]));
        }

        // \z anchors the match at the very end of the text, i.e. where the user just stopped typing.
        patternBuilder.Append(@"\z");

        return new Regex(patternBuilder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Checks whether <paramref name="textBeforeCursor"/> ends with a macro trigger.
    /// </summary>
    /// <param name="textBeforeCursor">The current line up to (not including) the separator the user just typed.</param>
    /// <param name="triggerStartIndex">Where the trigger starts within <paramref name="textBeforeCursor"/>.</param>
    /// <param name="triggerLength">How many characters the trigger covers.</param>
    /// <param name="expandedText">The replacement text with the number filled in.</param>
    /// <returns>True if a macro matched.</returns>
    public bool TryExpandAtEnd(string textBeforeCursor, out int triggerStartIndex, out int triggerLength, out string expandedText)
    {
        foreach (var compiledMacro in compiledMacros)
        {
            var triggerMatch = compiledMacro.TriggerPattern.Match(textBeforeCursor);
            if (!triggerMatch.Success)
            {
                continue;
            }

            // Fill the typed number into every {n} of the replacement.
            var numberGroup = triggerMatch.Groups[NumberGroupName];
            string typedNumber = numberGroup.Success ? numberGroup.Value : string.Empty;

            triggerStartIndex = triggerMatch.Index;
            triggerLength = triggerMatch.Length;
            expandedText = compiledMacro.Definition.Replacement.Replace(NumberPlaceholder, typedNumber, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        triggerStartIndex = 0;
        triggerLength = 0;
        expandedText = string.Empty;
        return false;
    }

    /// <summary>
    /// Expands every macro trigger in a finished piece of text, e.g. "Fix @730" → "Fix [Task 730](...)".
    /// Unlike typing, a trigger at the very end of the text counts too, since no separator follows it.
    /// The replacements are not searched again, so a replacement never triggers another macro.
    /// </summary>
    public string ExpandAll(string inputText)
    {
        if (!HasMacros || string.IsNullOrEmpty(inputText))
        {
            return inputText;
        }

        var expandedTextBuilder = new System.Text.StringBuilder();
        // Everything before this index has already been copied (or replaced) into the result.
        int copiedUpToIndex = 0;

        // A trigger can only end where a word ends: before whitespace, a terminator, or the end of the text.
        for (int wordEndIndex = 1; wordEndIndex <= inputText.Length; wordEndIndex++)
        {
            bool isWordEnd = wordEndIndex == inputText.Length
                || char.IsWhiteSpace(inputText[wordEndIndex])
                || TerminatorCharacters.Contains(inputText[wordEndIndex]);
            if (!isWordEnd)
            {
                continue;
            }

            if (TryExpandAtEnd(inputText[..wordEndIndex], out int triggerStartIndex, out _, out string expandedText)
                && triggerStartIndex >= copiedUpToIndex)
            {
                expandedTextBuilder.Append(inputText, copiedUpToIndex, triggerStartIndex - copiedUpToIndex);
                expandedTextBuilder.Append(expandedText);
                copiedUpToIndex = wordEndIndex;
            }
        }

        expandedTextBuilder.Append(inputText, copiedUpToIndex, inputText.Length - copiedUpToIndex);
        return expandedTextBuilder.ToString();
    }
}
