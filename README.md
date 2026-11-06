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
- **Links** menu: your own list of name + URL shortcuts that open in the default browser.
  Add, edit, reorder or remove them under Links → Edit links. Stored in the same `settings.json`.

- **Bookshelf** (left panel, **Ctrl+B** to show or hide): collect Markdown files from anywhere on disk in one list.
  Add them with **Add...**, **Ctrl+D** (adds the open file), or by dropping files or a folder onto the panel.
  The list shows file names only (hover for the full path). Drag a file up or down to put the shelf in your own order
  (or use Alt+Up / Alt+Down); right-click → *Sort by name* puts it back in alphabetical order.
  Click a file to open it; right-click for *Show in folder*, *Copy path* and *Remove* (the file itself is never deleted).
  Moved or deleted files are greyed out. The bookshelf is stored in `%APPDATA%\MarkdownViewer\settings.json`.
- **Several bookshelves**: the drop-down at the top of the panel switches between shelves, and the **⋯** button
  creates, renames or deletes them (e.g. *Documentation*, *Post-its*). The **Standard** shelf always exists and is
  the one shown when the app starts. A file can be on several shelves: right-click → *Add to bookshelf* or
  *Move to bookshelf*. Each shelf keeps its own order. Deleting a shelf never deletes the files.
- **Zip all** (File → Zip all): backs up every bookshelf file plus `settings.json` into a timestamped
  `MarkdownViewer-backup-<date>_<time>.zip` next to the exe (or a place you pick, if that folder is read-only).
  Files keep their original folder structure under `files/` (e.g. `files/C/Notes/README.md`), so files with the same
  name never clash, and `backup-contents.txt` lists each original path plus any missing files. To move to another
  computer, unzip it and copy `settings.json` to `%APPDATA%\MarkdownViewer\`.
- **Todo list** (Todo menu): a plain `todo.md` next to the exe that you can arrange however you like.
  **Ctrl+Shift+T** asks for a task and adds `- [ ] task` at the top of the section *Todo → Section for new tasks*
  (default `## Todo`; matched by heading text, added at the end of the file if missing). If the section already has
  tasks, the new one goes directly above the first of them, so text you put between the heading and the list stays put.
  Nothing else in the file is touched. Macros (e.g. `@730`) expand in the task text too. **Ctrl+T** opens the list. Zip all includes `todo.md` too.
- **Task check boxes** in any document are clickable: ticking one changes `[ ]` to `[x]` (or back) in the file
  and saves it straight away.
- **Properties** (under the bookshelf): location, size, created and modified dates, characters, words, lines,
  paragraphs, headings per level, links, images, code blocks, tables, tasks done and an estimated reading time
  for the open document. The numbers follow along while you edit.

Supported Markdown: CommonMark plus tables, task lists, strikethrough, footnotes, emoji and auto-linked URLs.
**Mermaid diagrams**: a ```` ```mermaid ```` code block is drawn as a diagram (flowcharts, sequence, class, state, ER,
Gantt, pie, mindmaps and the rest of Mermaid). This uses the Microsoft Edge WebView2 Runtime that comes with
Windows 10 and 11, started invisibly only when a document has a diagram. mermaid.js is built into the exe, so it
works offline and diagram text never leaves your computer. If a diagram has an error (or WebView2 is missing),
the code is shown with a short note explaining why.
Raw HTML is shown as source text, and SVG images are not supported (their alt text is shown instead).

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/MarkdownViewer
dotnet publish src/MarkdownViewer -c Release -o publish   # produces publish/MarkdownViewer.exe
```

## Dependencies

- [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause) for parsing Markdown. Rendering into native WPF is done in `src/MarkdownViewer/Rendering`.
- [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) (BSD-style) to run mermaid.js for diagrams.
- [Mermaid](https://mermaid.js.org) 12.3.0 (MIT), bundled as `src/MarkdownViewer/Assets/Mermaid/mermaid.min.js`.
  To update it, replace that file with `dist/mermaid.min.js` from the [npm package](https://www.npmjs.com/package/mermaid).
