# Markdown Viewer

Open a Markdown file with **File → Open** (Ctrl+O), or drag and drop one onto this window.

## Shortcuts

| Action                     | Shortcut          |
| -------------------------- | :---------------: |
| Open a file                | Ctrl+O            |
| Toggle side-by-side editor | Ctrl+E            |
| Save / Save as             | Ctrl+S / Ctrl+Shift+S |
| Reload from disk           | F5                |
| Find in document           | Ctrl+F            |
| Zoom                       | Ctrl+Mouse wheel  |
| Print                      | Ctrl+P            |

## What gets rendered

- *Italic*, **bold**, ~~strikethrough~~ and `inline code`
- [Links](https://commonmark.org) and [jumps within the document](#tips)
- Nested lists
  1. Numbered
  2. Items
- Tables, block quotes, images and footnotes[^1]

- [x] Task lists
- [ ] Unchecked items

> Block quotes look like this.

```csharp
// Code blocks keep their formatting
Console.WriteLine("Hello, Markdown!");
```

Mermaid code blocks are drawn as diagrams:

```mermaid
flowchart LR
    Write[Write Markdown] --> View[View it here]
    View --> Share[Share the file]
```

---

## Tips

The file is watched while open: if another program changes it, the view refreshes automatically
(as long as you have no unsaved edits).

**Macros:** in the editor, type `@730` followed by a space and it becomes a link to task 730.
Change or add macros under **Tools → Macros**.

**Links:** keep the sites you use often under the **Links** menu. Add them with **Links → Edit links**.

[^1]: Footnotes appear at the bottom of the document.
