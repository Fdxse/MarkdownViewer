namespace MarkdownViewer.Links;

/// <summary>
/// One entry in the Links menu: a display name and the address it opens in the default browser.
/// </summary>
/// <remarks>
/// Settable properties and a parameterless constructor let it be stored as JSON in
/// settings.json and edited directly in the dialog's grid.
/// </remarks>
public class SavedLink
{
    /// <summary>The text shown in the Links menu, for example "Azure DevOps board".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The address to open, for example "https://devops/dev/_boards".</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Creates a copy, so the dialog can be cancelled without changing the live list.</summary>
    public SavedLink Clone() => new()
    {
        Name = Name,
        Url = Url,
    };

    /// <summary>
    /// The address ready to hand to Windows. An address typed without a scheme, such as
    /// "github.com", gets "https://" in front so it opens in the browser instead of failing.
    /// </summary>
    public string GetOpenableAddress()
    {
        string trimmedUrl = Url.Trim();
        bool hasScheme = Uri.TryCreate(trimmedUrl, UriKind.Absolute, out Uri? parsedAddress)
            && !string.IsNullOrEmpty(parsedAddress.Scheme)
            // "C:\folder" parses as scheme "c"; a one-letter scheme is a drive letter, not a web address.
            && parsedAddress.Scheme.Length > 1;
        return hasScheme ? trimmedUrl : "https://" + trimmedUrl;
    }
}
