# DDL Panel Ergonomics — Font Zoom, Word-Wrap Toggle, Separated Line-Number Gutter

**Date:** 2026-05-29
**Status:** Approved design, pending implementation plan

## Problem

The two-panel side-by-side DDL comparison in the comparison results view has three ergonomic gaps:

1. **No font-size control.** The DDL font is hardcoded to 12pt. Users want to zoom in/out via `Ctrl+MouseWheel` or `Ctrl + +/-`.
2. **No word-wrap toggle.** A `FlowDocument` wraps to width by default with no way to turn it off; long SQL lines are hard to read.
3. **Line numbers are part of the copyable text.** Line numbers are rendered as `Run`s *inside* each paragraph, so selecting and copying SQL also copies the leading line numbers — the pasted text can't be executed directly.

## Affected code (current state)

- DDL panels: `RichTextBox` controls `DdlBoxA` / `DdlBoxB` in `src/SQLParity.Vsix/Views/ResultsView.xaml`, bound to `FlowDocument`s.
- Diff rendering: `src/SQLParity.Vsix/Helpers/SimpleDiffHighlighter.cs` — LCS-based aligned diff with padding rows and per-character inline highlights. Line numbers are emitted as inline `Run`s in `MakeNumberedParagraph` / `MakeNumberedInlineDiff`. Font size is hardcoded in `CreateBaseDocument` (`FontSize = 12`).
- View code-behind: `src/SQLParity.Vsix/Views/ResultsView.xaml.cs` — builds the documents in `UpdateDdlDiff`, manages synced scrolling, error-banner fallback (`RawDoc` / `PlaceholderDoc`).
- Settings: `src/SQLParity.Vsix/Options/SQLParityOptionsPage.cs` (a `DialogPage`) with an existing `ShowLineNumbers` option; read via `OptionsHelper.GetOptions()`, persisted via `DialogPage.SaveSettingsToStorage()`.

## Decisions (confirmed)

- **Approach:** Incremental on the current `RichTextBox` / `FlowDocument` panels. The hand-tuned aligned diff (padding rows, inline char highlights, colors) stays untouched.
- **Line numbers:** A real visual gutter column — numbers are never in the text at all.
- **Word-wrap default:** Off (horizontal scroll).
- **Persistence:** Font size and word-wrap persist across sessions via Tools → Options, alongside `ShowLineNumbers`.

## Key design insight

Removing line numbers from the `FlowDocument` and painting them in a gutter solves the copy problem *by construction* (the RichTextBox holds pure SQL) and also solves the wrap-alignment problem: the gutter draws each number at the pixel Y of its paragraph's start position (via `TextPointer.GetCharacterRect`), so a wrapped continuation row simply has no number — exactly like VS Code. The same mechanism is correct whether wrap is on or off.

## Components

### 1. `LineNumberGutter` (new control)

A thin `FrameworkElement`-derived control pointed at a target `RichTextBox`.

- **Responsibility:** Draw right-aligned gray line numbers aligned to each paragraph of the target.
- **Interface:** A `Target` (the `RichTextBox`) and the current font size; a `ShowNumbers` flag. Exposes nothing else.
- **Rendering:** Override `OnRender`. Determine the visible paragraph range (walk from the top visible `TextPointer` forward until past the viewport bottom — bounds work to visible lines so large procs scroll smoothly). For each visible paragraph, read its line number from `Paragraph.Tag`; if non-null, draw the number with `FormattedText` at the paragraph's `ContentStart.GetCharacterRect(...).Top`, right-aligned within the gutter width. Untagged (padding) paragraphs draw nothing.
- **Invalidation:** Hooks the target's `ScrollChanged` (find its inner `ScrollViewer`), `SizeChanged`, and `LayoutUpdated`/document changes; calls `InvalidateVisual()`.
- **Width:** Auto-sized to fit the widest expected number at the current font (measure with `FormattedText`); scales with font size.
- **Divider:** Draws a thin right-edge divider line between the gutter and the text.

This is a self-contained unit: given a RichTextBox whose paragraphs are tagged with line numbers, it renders the gutter; it depends only on WPF.

### 2. `SimpleDiffHighlighter` changes

- Stop prepending the line-number `Run` in `MakeNumberedParagraph` and `MakeNumberedInlineDiff`.
- Instead stamp each numbered paragraph with its line number: `paragraph.Tag = lineNum`. Padding paragraphs (`MakePaddingParagraph`) stay untagged (`Tag == null`).
- No change to alignment, LCS, inline char-diff highlighting, or colors.
- Remove the font-size hardcode coupling so the document font can be set by the caller (keep a sensible default; the view overrides it).
- The `ShowLineNumbers` static flag is no longer needed inside the highlighter for emitting numbers (numbers are always tagged); gutter visibility is decided by the view. (Keep or remove per cleanliness during implementation.)

### 3. `ResultsView.xaml` changes

- Wrap each DDL `RichTextBox` in a `[gutter | text]` layout (e.g. a `DockPanel` with the `LineNumberGutter` docked left, or a 2-column `Grid`) inside the existing bordered container `DdlBorderA` / `DdlBorderB`.
- Add a small **"Wrap"** toggle (checkbox or toggle button) to the DDL header row (Row 1 of the DDL grid).

### 4. `ResultsView.xaml.cs` changes

- Apply the current font size to both `FlowDocument`s (including the `RawDoc` / `PlaceholderDoc` fallbacks) and to both gutters; trigger gutter re-render.
- Apply word-wrap to both panels together: **off** ⇒ set `Document.PageWidth` to the longest line's width so lines don't wrap and a horizontal scrollbar appears only when needed (fall back to viewport width for short content, so short objects don't get a permanent scrollbar); **on** ⇒ `Document.PageWidth = NaN` (auto-wrap to viewport).
- Input handlers:
  - `Ctrl + MouseWheel` on either panel ⇒ font size ±1 (reliable path).
  - `Ctrl + +` / `Ctrl + -` ⇒ font size ±1.
  - `Ctrl + 0` ⇒ reset to default (12).
  - Clamp font size to `[6, 40]`.
- Persistence: apply changes immediately in-memory; persist the value to Options **debounced** (e.g. ~500 ms after the last change) so `Ctrl+MouseWheel` spam doesn't thrash `SaveSettingsToStorage`.
- "Wrap" toggle updates both panels and persists `WrapDdlPanels`.

### 5. `SQLParityOptionsPage` changes

Add two options under the existing **Comparison** category:

- `DdlFontSize` (`int`, default `12`) — font size for the DDL diff panels.
- `WrapDdlPanels` (`bool`, default `false`) — wrap long lines in the DDL diff panels.

`ShowLineNumbers` is retained and now governs whether the gutter is drawn.

## Data flow

```
SchemaComparator → DDL strings (A, B)
  → SimpleDiffHighlighter.CreateAlignedDiffDocuments
      → FlowDocument A / B (paragraphs tagged with per-side line numbers; pure SQL text)
  → assigned to DdlBoxA / DdlBoxB
  → LineNumberGutterA / B target those boxes, render numbers from Paragraph.Tag
Options (DdlFontSize, WrapDdlPanels, ShowLineNumbers)
  → applied in UpdateDdlDiff and on user input (Ctrl+wheel / Ctrl+/- / Wrap toggle)
```

## Edge cases

- **Error-banner / raw-DDL fallback:** `RawDoc` / `PlaceholderDoc` produce untagged paragraphs ⇒ gutter draws nothing, but font size is still applied.
- **Short / empty documents:** with wrap off, compute `PageWidth` from the actual longest line and fall back to viewport width so there's no permanent horizontal scrollbar.
- **Large procedures (thousands of lines):** gutter renders only the visible paragraph range to keep scrolling smooth.
- **Synced scrolling:** the existing `EnsureSyncScrollingHooked` keeps both panels in vertical sync; each gutter follows its own box via its scroll hook.
- **`Ctrl + +/-` keyboard gestures may be swallowed by VS command routing** in a tool window. `Ctrl+MouseWheel` is reliable. If the keys are intercepted, fall back to mouse-wheel-only zoom plus the Options value. Verify during testing.

## Verification

- **Primary:** Manual verification in a real SSMS instance — bump `version.txt` and install to a regular SSMS instance (never the Exp hive), then exercise: zoom via wheel and keys, wrap toggle on/off, select-copy-paste SQL and confirm no line numbers are included, and confirm settings survive a restart.
- **Optional logic-level assertions:** the FlowDocument text contains no leading number prefix (clean-copy guarantee) and paragraph `Tag`s carry the expected per-side line numbers with padding rows untagged. (Requires a WPF-capable test host; the current `SimpleDiffHighlighter` is not unit-tested today.)

## Out of scope

- Migrating to a different editor control (AvalonEdit).
- Syntax highlighting.
- Independent (unsynced) font size or wrap per panel — both panels always share these settings.
