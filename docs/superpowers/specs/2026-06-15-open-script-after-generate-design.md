# Open Script After Generate

## Goal
After **Generate Script** writes the .sql file to disk, automatically open it as a new SQL query tab in the active SSMS window. Make the behavior controllable via Tools → Options and default it on.

## Motivation
Today the Generate Script flow ends with a "Script saved to *path*" MessageBox. The user then has to find the file, switch context to SSMS, and open it themselves before they can review or execute. Opening it directly closes that loop — for the common case where the next thing the user does is review/execute the script — without breaking the "save to disk" expectation users rely on for archival or sharing.

## Scope
**In scope**
- Adding a new Tools → Options setting (`OpenScriptAfterGenerate`).
- Wiring the post-save open call into `ComparisonHostViewModel.GenerateScript()`.

**Out of scope**
- Apply Live: no file is written; nothing to open.
- Folder-mode sync (`ApplyFolderWriteAsync`): writes many files; not what the user requested.
- Any change to the SaveFileDialog or the existing summary MessageBox.

## Design

### Option
Add to `src/SQLParity.Vsix/Options/SQLParityOptionsPage.cs`:

```csharp
[Category("Script Output")]
[DisplayName("Open Script After Generate")]
[Description("When enabled, the generated .sql file is opened in SSMS's editor immediately after it's saved.")]
[DefaultValue(true)]
public bool OpenScriptAfterGenerate { get; set; } = true;
```

`Script Output` is a new category, sibling to the existing `Comparison`, `Performance`, `External Diff Tool`.

### Wiring
In `ComparisonHostViewModel.GenerateScript()`, between the `File.WriteAllText` call (currently ComparisonHostViewModel.cs:534) and the existing summary MessageBox (`Script saved to {0}\n{1} changes, {2} destructive.`):

1. Read the option via `OptionsHelper.GetOptions()` (existing helper, used by other options).
2. If `OpenScriptAfterGenerate` is `true`, call `VsShellUtilities.OpenDocument(serviceProvider, savePath)` on the UI thread to open the file in SSMS's editor.
3. The existing MessageBox runs regardless, so the user always sees the change/destructive counts.

### Error handling
A failure inside `OpenDocument` (no service provider, file lock, unregistered editor for `.sql`) must not bury the save success. The save already happened; the file is on disk. Wrap the open in `try { … } catch (Exception ex) { Debug.WriteLine(...) }` and fall through to the MessageBox.

### Threading
`VsShellUtilities.OpenDocument` requires the UI thread. The save block uses `await Task.Run(...)`; the continuation after `await` resumes on the captured synchronization context (the UI thread), so a direct call there is correct.

### Service provider
`VsShellUtilities.OpenDocument(IServiceProvider, string)` needs the package's service provider. `SQLParityPackage` is an `AsyncPackage` (implements `IServiceProvider`) and is already exposed as a singleton via `SQLParityPackage.Instance` (used by `OptionsHelper.GetOptions()`). The open call passes `SQLParityPackage.Instance` directly — no new singletons or static handoffs.

## Test plan
`VsShellUtilities.OpenDocument` can't be cleanly unit-tested (it requires the live VS service provider and a registered editor for `.sql`). Coverage is the same level as the existing `SaveFileDialog` and result `MessageBox` calls in the same method — exercised by manual smoke-test, not unit tests.

**Manual smoke checks before shipping**
- Default install: Generate Script → file opens automatically as a new SQL query tab in SSMS; the "Script saved to" MessageBox still appears with counts.
- Option disabled: Generate Script → file is saved, MessageBox appears, but no tab opens.
- Cancel from SaveFileDialog: behavior unchanged (no save, no open, no MessageBox).
- Open failure (simulated by deleting the saved file between `File.WriteAllText` and the open call, or pointing a temporary `.sql` association to a missing handler): MessageBox still appears, no crash, no second error dialog.

## Versioning
Patch bump to `1.4.4`. New behavior is on by default — call that out in the release notes alongside the new Tools → Options toggle for users who prefer the old behavior.
