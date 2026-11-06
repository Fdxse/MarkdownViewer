# Markdown Viewer

A small Windows app for **viewing** Markdown documents, with a simple side-by-side editor for quick changes.
It ships as one self-contained `MarkdownViewer.exe`: no installer and no .NET runtime needed.

## Download

Grab `MarkdownViewer.exe` from the latest [release](../../releases/latest). Every pull request merged to `main` produces a new release automatically.

## Using it

- Open a file with **File → Open** (Ctrl+O), by dragging it onto the window, or with *Open with...* in Explorer.
- **Ctrl+E** toggles the editor; the preview updates as you type. **Ctrl+S** saves.
- **Ctrl+F** finds text, **Ctrl+mouse wheel** zooms, **F5** reloads, **Ctrl+P** prints.
- The open file is watched: if another program changes it, the view refreshes (unless you have unsaved edits).
- Links to other `.md` files open in the viewer, `#heading` links jump within the document, and web links open in your browser.
- **Macros** (Tools → Macros): typing a trigger in the editor and then a space, Enter or punctuation replaces it with a template.
  `{n}` stands for a number, so the default macro turns `@730` into `[Task 730](https://devops/dev/_workitem_edit/730)`.
  Ctrl+Z undoes an expansion. Macros are stored in `%APPDATA%\MarkdownViewer\settings.json`.

Supported Markdown: CommonMark plus tables, task lists, strikethrough, footnotes, emoji and auto-linked URLs.
Raw HTML is shown as source text, and SVG images are not supported (their alt text is shown instead).

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/MarkdownViewer
dotnet publish src/MarkdownViewer -c Release -o publish   # produces publish/MarkdownViewer.exe
```

## Dependencies

- [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause) for parsing Markdown. Rendering into native WPF is done in `src/MarkdownViewer/Rendering`.
