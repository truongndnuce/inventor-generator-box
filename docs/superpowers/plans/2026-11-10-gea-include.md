# Implementation plan: `INCLUDE lib:` for .gea scripts

Design: `docs/superpowers/specs/2026-11-10-gea-include-design.md`. Each task ends with a commit; tests run with `dotnet test be/Cadflow.Api.Tests` (needs a local PostgreSQL, see `docs/database.md`). The Inventor add-in cannot run on this machine, it can only be built.

## Task 1: The `GeaIncludes` expander (Core, pure text)

**New file:** `InventorAddin/InventorAddin.Core/Gea/GeaIncludes.cs`; link into `be/Cadflow.Api/Cadflow.Api.csproj` (it already links `Gea\*.cs`, so it is picked up). **Test:** `be/Cadflow.Api.Tests/GeaIncludesTests.cs`.

1. Write the tests first (red). Use a fake resolver `name => dict.GetValueOrDefault(name)`:
      - Expanding inside `BEGIN_FORM` inserts the FORM body of the library; inside `BEGIN_BUILD` it inserts the BUILD body; a library that lacks that block inserts nothing.
      - The library header (`GEA_VERSION`, `PRODUCT_NAME`, `TARGET`, `PARAM_PREFIX`) does not go into the result; `PARAM_PREFIX` goes into `LibPrefixes`.
      - `Lines[i]` maps to the right `(File, Line)` for lines of the parent and lines of the library.
      - Errors (one test each, `Diagnostics` has `GEA1012`...): `INCLUDE` outside a block; a missing library; a name with `/`, `\`, `..` or outside `^[A-Za-z0-9_-]{1,64}$`; a cycle A↔B; a second include of the same kind of block; depth > 8; a null resolver.
2. Implementation:
   ```csharp
   public sealed record GeaSourceLine(string File, int Line);
   public sealed record GeaExpansion(string Text, IReadOnlyList<GeaSourceLine> Lines,
       IReadOnlyList<string> LibPrefixes, IReadOnlyList<GeaDiagnostic> Diagnostics);
   public static class GeaIncludes
   {
       public const int MaxDepth = 8;
       public static GeaExpansion Expand(string text, Func<string, string?>? resolveLib, string fileName = "script");
   }
   ```
      Scan line by line, tracking the current block with the `BEGIN_FORM`/`BEGIN_BUILD` and `END_*` lines (skip `!` comments and lines inside strings). A line `INCLUDE lib:<name>` (case-insensitive, a trailing comment allowed) is replaced with the body of the block of the same kind of the library, recursively with the set of open libraries (to catch cycles) and the depth. The block body = the lines between `BEGIN_x` and `END_x` of the library (not those two lines themselves). Error codes `GEA1010`-`GEA1015` for the include group, the position = the line of the `INCLUDE` in the file that contains it.
3. Tests green, commit `Add GeaIncludes expander`.

## Task 2: Compile expands + prefix + line mapping

**Edit:** `Gea/GeaInterpreter.cs` (`GeaScript.Compile`), `Gea/GeaAst.cs` (`GeaProgram`), `Gea/GeaChecker.cs`. **Test:** `GeaIncludeCompileTests.cs`.

1. Tests first:
      - A parent script `PARAM_PREFIX GT_` + `INCLUDE` of a library `PARAM_PREFIX TR_` (one parameter `TR_X`, one `GT_Y`): `Compile` has no error, `Parameters` has both.
      - An unknown parameter (`ZZ_Q`) still gets `GEA2030`.
      - An error inside the library has a `Message` that starts with `tank-lib.gea:` and a `Line` that is the line in the library; an error in the parent after the insertion reports the right original line of the parent.
      - No resolver passed and an `INCLUDE` present → error `GEA1012`, no exception.
      - A script with no `INCLUDE` gives exactly the same result as before (regression: re-run the existing Gea tests if any, and the parameter count test of `transformer.gea`).
2. Implementation:
      - `GeaProgram` gets `IReadOnlyList<string> ExtraParamPrefixes { get; init; }` (empty by default); the parser stays as it is, `Compile` uses `with`/reassigns after parsing (change `init` to `set` if needed).
      - `Compile(string text, Func<string,string?>? resolveLib = null, string fileName = "script")`: calls `Expand`, parses `expansion.Text`, sets `ExtraParamPrefixes = LibPrefixes`, merges `expansion.Diagnostics`, and after the check, for each diagnostic maps `Line` through `Lines` and, if `File != fileName`, prefixes the `Message` with `"<File>:<Line>: "`. Sorted as before.
      - `GeaScript` gets `ExpandedText` (the text after expansion; equal to `Text` when there is no INCLUDE).
      - Checker: the two prefix checks (`GeaChecker.cs` around lines 337 and 393) use a function `HasAllowedPrefix(name)` = starts with `ParamPrefix` or any prefix in `ExtraParamPrefixes`; line 488 (`DeclareHint`) keeps its meaning.
3. Test xanh, commit `Compile expands INCLUDE and accepts library prefixes`.

## Task 3: The `tank-base-cover` library and `transformer.gea` using it

**New file:** `InventorAddin/InventorAddin.Core/Scripts/lib/tank-base-cover.gea`. **Edit:** `Scripts/transformer.gea`, `InventorAddin.Core.csproj` (copy `Scripts\lib\*.gea` to the add-in output), `be/Cadflow.Api/Cadflow.Api.csproj` (Content link `Scripts\lib\tank-base-cover.gea` → `Scripts\lib\`, `PreserveNewest`). **Test:** `TransformerIncludeEquivalenceTests.cs`.

1. Test first (red because the library does not exist yet): read the current `transformer.gea` with git (`git show HEAD:...` saves the original as the fixture `be/Cadflow.Api.Tests/Fixtures/transformer.original.gea`) and the new version with `INCLUDE`; compare `Compile(...)` of the two:
      - the same parameter list (name, type, default, min, max, label, group),
      - the same `EvaluateFormNumbers` result for the defaults and a few value sets (including rule violations),
      - the same BUILD command sequence (compare `ExpandedText` with whitespace normalized and blank/comment lines removed).
2. Extract the group: the block `BEGIN_GROUP "Tank, base and cover"` (lines 20–28) becomes the FORM body of the library; the BUILD part "Base, tank and cover" (base/tank/cover, `hTank`, `hCover`, `hTop` and the three `SET_REF_NAME`) becomes the BUILD body. The `DECLARE_VARIABLE` of those variables moves into the library if only the library uses them, otherwise it stays in the parent. The library has the header `GEA_VERSION 1`, `PRODUCT_NAME`, `PARAM_PREFIX TR_`, `TARGET PART`.
3. `transformer.gea`: replace those blocks with `INCLUDE lib:tank-base-cover` at exactly the old position in both FORM and BUILD.
4. Test xanh; commit `Extract tank-base-cover library; transformer.gea uses INCLUDE`.

## Task 4: The server loads libraries and stores the expanded copy

**New file:** `be/Cadflow.Api/GeaLibraries.cs` (`Resolve(name)` reads `AppContext.BaseDirectory/Scripts/lib/<name>.gea`, null if missing; an invalid name returns null). **Edit:** `be/Cadflow.Api/JobService.cs` (the three `GeaScript.Compile` places at lines 30, 81, 115 pass `GeaLibraries.Resolve`; line 60 expands the bundled copy before `scripts.Save`; import stores `script.ExpandedText` instead of `clean`). **Test:** `ServerIncludeTests.cs`.

1. Test first (use `TestDb`, write a test library into `Scripts/lib` of the test folder or use the real `tank-base-cover`):
      - `Import` of text with `INCLUDE lib:tank-base-cover` returns a `form` with the parameter `TR_TankLength`; `ScriptStore.Get(scriptId)` no longer contains `INCLUDE`.
      - Creating a `transformer` job (the built-in path): `ScriptStore.Get(job.ScriptSha256)` no longer contains `INCLUDE`, and `claim` returns the expanded `ScriptText`.
      - Importing a script that uses a library that does not exist → 400 with `diagnostics`.
2. Implement as above; `ScriptStore.ReadBundled` stays as it is (read only), the expansion lives in `JobService`. Check that `Cadflow.Api.csproj` copies `Scripts/lib` to the output when running tests.
3. Test xanh, commit `Server expands INCLUDE on import and job creation`.

## Task 5: The add-in opens files with INCLUDE (build only)

**Edit:** `InventorAddin/InventorAddin.Core/ViewModels/ScriptViewModel.cs` (line ~537): `GeaScript.Compile(_scriptText, GeaLibraryResolver.For(directoryOfOpenFile))`. `InventorJobExecutor.cs:112` does not change (the job receives the expanded copy). **New file:** `Core/Gea/GeaLibraryResolver.cs`: tries `<file folder>/lib/<name>.gea` and `<file folder>/<name>.gea`, then `AppContext.BaseDirectory/Scripts/lib/<name>.gea`; accepts valid names only.

1. Add a pure test for `GeaLibraryResolver` in `Cadflow.Api.Tests` (if the file is linked) or check by reading the code if it cannot be linked.
2. Build the add-in solution if the machine has the SDK/Inventor references; if not, say clearly that it could not be built. Commit `Add-in resolves INCLUDE libraries`.

## Task 6: Documentation

- `sa-docs/gea-language-spec.md`: add the section `INCLUDE lib:` (syntax inside a block, shared variables, library prefix, errors `GEA1010-1015`, limits), update the P1 table and mark Q1 as settled (share variables).
- `sa-docs/smartassembly-to-inventor-mapping.md` and `docs/smartassembly-to-inventor-mapping.md`: D6 settled.
- `README.md`: one line about `Scripts/lib` and how to add a library.
- Commit `Document INCLUDE`.

## Acceptance

- `dotnet test be/Cadflow.Api.Tests` is all green (22 old tests + the new tests).
- Run the backend, place a `transformer` job through `POST /api/jobs`: the job is created, `claim` returns the expanded script.
- Importing a `.gea` with `INCLUDE` through `POST /api/gea/import` returns the right form.
- `git status` is clean, no build files or `.env` committed.
