namespace MarkdownViewer.Macros;

/// <summary>
/// One user-defined text macro. When the user types something matching <see cref="Trigger"/>
/// in the editor, it is replaced with <see cref="Replacement"/>.
/// </summary>
/// <remarks>
/// The class has settable properties and a parameterless constructor so it can be
/// stored as JSON and edited directly in the settings dialog's grid.
/// </remarks>
public class MacroDefinition
{
    /// <summary>
    /// What to type, for example "@{n}". The placeholder {n} matches any whole number;
    /// everything else must be typed exactly (letter case is ignored).
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// The text that replaces the trigger, for example "[Task {n}](https://devops/dev/_workitem_edit/{n})".
    /// Every {n} is replaced by the number that was typed.
    /// </summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>Lets a macro be switched off without deleting it.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Creates a copy, so the settings dialog can be cancelled without changing the live macros.</summary>
    public MacroDefinition Clone() => new()
    {
        Trigger = Trigger,
        Replacement = Replacement,
        IsEnabled = IsEnabled,
    };
}
