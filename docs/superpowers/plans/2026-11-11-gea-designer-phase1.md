# Implementation plan: the Product Designer, phase 1 (parameters, rules, generate, check, download)

Design: `docs/superpowers/specs/2026-11-11-gea-designer-design.md` (section 11, phase 1). Each task ends with a commit; tests run with `dotnet test be/Cadflow.Api.Tests` (needs a local PostgreSQL, see `docs/database.md`). Nothing in this phase needs Inventor: everything is server-side text generation, the compiler and the simulation Host, plus a web page. The front end has no automated tests in this repository, so Task 8 is verified by hand.

## Scope of phase 1

In: the design model; the generator model → `.gea`; the `!@DESIGNER` block and reading it back; `GET /api/gea/builders`; `POST /api/gea/designer/generate`, `/parse`, `/dryrun`; the page with tabs **Product, Parameters, Rules, Build (text), Text, Try it**; download of the `.gea`.

Out (phase 2 and later): the Parts tab (builders, Repeat, Variable steps as UI), "Convert" of hand-written files, `DRAWING_LEVEL`, Build preview job, AI draft, publishing as a named product, saving designs on the server.

Decided in the design and followed here: **a file without the `!@DESIGNER` block opens in text mode only** (no conversion); the generated file is the single source of truth for the server, worker and add-in (they are not changed in this phase).

The **Build (text)** tab is the one addition to the design's phase 1 list: the model keeps `steps` with a single `raw` step whose text the author types in, so a Designer file can already contain a real BUILD block (for example one copied from `box.gea`) while the Parts tab does not exist. Without it the generated BUILD block would be empty and the file useless to order.

## Facts the plan relies on (checked in the code)

- `GeaScript.Compile(text, resolveLib, fileName)` returns diagnostics (`GeaDiagnostic(Code, Severity, Line, Column, Message)`); `script.HasErrors`; `EvaluateFormValues(numbers, texts)`; `RunBuild(form, host)` with `GeaSimulationHost` for a dry run.
- A parameter name must start with the file's `PARAM_PREFIX` (`GeaProgram` checks it). `BEGIN_BUILD … END_BUILD` is **required** (error `GEA1002` if missing); `GEA_VERSION`, `PRODUCT_NAME`, `TARGET` are required.
- `USER_INPUT_PARAM` types: `LENGTH ANGLE INTEGER DOUBLE STRING` (not BOOL); options `MIN MAX STEP REQUIRED LABEL TOOLTIP`; for LENGTH/ANGLE a unit may follow the default (`100 mm`). `CHECKBOX_PARAM name TRUE|FALSE [LABEL] [TOOLTIP]`. `RADIOBUTTON_PARAM name "a" "b" [LABEL] [TOOLTIP] [DEFAULT "a"]`. `ASSERT <cond> <message> [WARN]`.
- `GeaForms.Describe(form)` already turns a computed form into the JSON the UI renders; `GeaForms.Diagnostics(script)` already formats diagnostics; limits `MaxScriptBytes` = 256 KB, `MaxFields` = 200.
- `GeaExport` (`be/Cadflow.Api/GeaExport.cs`) shows the `!@` block convention and has `Sha256`, `StripBlock`, `ParseBlock`; the Designer block follows it but is a separate block (marker `!@DESIGNER`), so exporting a job from a Designer product must not break (Task 4 covers it).
- `ScriptOrder.jsx` has a **local** `ScriptField` component; the Try it tab needs the same field renderer, so it is extracted in Task 8.

## Task 1: The design model and its validator

**New files:** `be/Cadflow.Api/Designer/DesignModel.cs`, `be/Cadflow.Api/Designer/DesignValidator.cs`. **New test:** `be/Cadflow.Api.Tests/DesignValidatorTests.cs`.

1. Model (records, `System.Text.Json` with web defaults, `version` = 1):
      - `DesignModel(int Version, DesignProduct Product, List<DesignGroup> Groups, List<DesignRule> Rules, List<DesignVar> Vars, List<DesignStep> Steps)`.
      - `DesignProduct(string Name, string Prefix, string Target)` (`Target` is `"PART"` only in this phase).
      - `DesignGroup(string Title, List<DesignParam> Params)`.
      - `DesignParam(string Name, string Type, string? Default, string? Unit, double? Min, double? Max, double? Step, bool Required, string? Label, string? Tooltip, string? VisibleWhen, List<string>? Options)`; `Type` is `LENGTH ANGLE INTEGER DOUBLE STRING CHECKBOX RADIO`. `Default` is a string so the same field holds a number, TRUE/FALSE or a choice, and is parsed by type.
      - `DesignRule(string Expr, string Message, bool Warn)`; `DesignVar(string Name, string Type, string Expr)` (`LENGTH ANGLE INTEGER DOUBLE`); `DesignStep(string Kind, string? Text)` with `Kind = "raw"` only in this phase (a model with any other kind is rejected with a clear message, so phase 2 can add kinds without breaking older files).
2. Tests first (red), one assertion per rule, each returning a list of `DesignIssue(Path, Message)` where `Path` is a model path such as `groups[1].params[2].name` (the UI puts the message next to that field):
      - Valid minimal model → no issues.
      - `product.prefix`: must match `^[A-Za-z][A-Za-z0-9]{0,15}_$` (same rule as `ComponentStarter.Validate`); `product.name` 1-80 chars, no quote, backslash or control character.
      - Parameter `name`: `^[A-Za-z][A-Za-z0-9_]{0,63}$`, **starts with the prefix**, unique across all groups and not equal to a variable name; a reserved word of the language (the keyword list in `GeaParser`) is rejected.
      - `type` in the allowed set; `LENGTH`/`ANGLE` `unit` in `GeaUnits` for that type (default `mm` / `deg`); `INTEGER` default must be a whole number; `STRING` has no min/max/step; `min <= max`; the default lies inside `[min, max]` (warning only, since the language does not forbid it).
      - `RADIO` needs ≥ 1 option, options unique, no quotes/backslashes/control characters, default one of the options; `CHECKBOX` default `TRUE` or `FALSE`.
      - Labels, tooltips, group titles and radio options: ≤ 200 chars; rule messages ≤ 400 chars (the shipped scripts have messages of about 150); no control characters; quotes and backslashes are **allowed** here because the generator escapes them (the lexer supports `\"` and `\\` inside strings; tested in Task 2).
      - Expressions (`rules[].expr`, `vars[].expr`, `visibleWhen`): non-empty, ≤ 500 chars, one line, and outside a quoted string they must not contain `!` (comment), `/*` (block comment) or `\` (line join); their real validity is decided by the compiler in Task 3, not here.
      - A rule or variable that mentions a parameter with `visibleWhen` gets a **warning** (the issue type has `IsWarning`).
      - Limits: ≤ 200 parameters in total, ≤ 50 groups, ≤ 100 rules, ≤ 100 variables, raw build text ≤ 100 KB, whole model JSON ≤ 256 KB.
      - A `steps` entry with `kind != "raw"` → "This version of the Designer only supports raw steps".
3. Implement `DesignValidator.Validate(DesignModel)`.
4. Commit `Add the design model and validator`.

## Task 2: The generator (model → `.gea`)

**New file:** `be/Cadflow.Api/Designer/DesignGenerator.cs`. **New tests:** `DesignGeneratorTests.cs`; fixtures `be/Cadflow.Api.Tests/Fixtures/designer/*.json` and one golden `box-form.gea`.

1. Tests first (red):
      - **Compiles**: for each fixture model, `GeaScript.Compile(text, GeaLibraries.Resolve)` has no errors. Fixtures: a minimal model; a model with every parameter type and a `visibleWhen`; a model with a warning rule and a variable; a model whose raw build text is the BUILD of `box.gea`.
      - **Equivalent to a shipped script**: a fixture `box-form.design.json` reproduces the FORM and the `ASSERT` lines of `box.gea` with the box BUILD as raw text; the generated script and `box.gea` give the **same form** (`EvaluateForm`: same field names, kinds, defaults, min, max, required, groups, labels) and the same validation result for a set of value tables (valid values; wall too thick; lid lip too long). This is the main proof that the generator speaks the language correctly.
      - Trace comparison between `box.gea` and the generated box ignores the line number that `PRINT` carries in the trace (it differs between the two files); every other command must be identical.
      - **Golden file**: a fixed model generates exactly the checked-in text (guards the stable order and the comments). The golden file `Fixtures/designer/all-types.gea` was produced by the generator once and reviewed by eye; the tests then keep it fixed.
      - **Escaping**: a label with `"` and `\`, and a message with both, produce a file that compiles and whose parsed label/message equals the original; a parameter name or prefix containing a newline is rejected by the validator before the generator runs (the generator throws if called with an invalid model).
      - **Order and shape**: header (`GEA_VERSION 1`, `PRODUCT_NAME`, `PARAM_PREFIX`, `TARGET`), then `BEGIN_FORM` with groups in order, then variables and `ASSERT`s, then `BEGIN_BUILD`; `visibleWhen` wraps the single param in `IF … END_IF`; `warn: true` appends `WARN`; LENGTH default written with its unit; INTEGER with no decimal point; STRING default quoted; RADIO `DEFAULT` only when it is not the first option.
      - **Line map**: `Generate` returns `(Text, LineMap)` where `LineMap` lists `(FirstLine, LastLine, Path)` for every parameter, group, rule, variable and the build block; a test checks that the line of `USER_INPUT_PARAM <name>` is inside the entry of that parameter.
      - (The `!@DESIGNER` block is **not** part of `Generate`: `Generate` returns the body only; Task 4 appends the block and tests it. This keeps the body hash in Task 4 simple.)
2. Implement `DesignGenerator.Generate(DesignModel) → DesignOutput(string Text, IReadOnlyList<LineEntry> LineMap)`. Output conventions (these fix the golden file):
      - LF line endings, two-space indent inside blocks, one blank line between sections, UTF-8 without BOM, exactly one newline at the end.
      - Each section starts with a short English comment, in the style of `ComponentStarter` (`! Parameters: …`), so a file opened in a text editor explains itself. User-written free text only ever goes into quoted strings or expressions, never into comments, so it cannot break out of a comment line.
      - `IF`-guarded parameters are written inside their group. Rules that reference a parameter hidden by `visibleWhen` are the author's responsibility; the validator emits a warning (not an error) when a rule or variable mentions a parameter that has `visibleWhen`.
      - `DECLARE_VARIABLE` + assignment for each variable, placed after the FORM params and before the `ASSERT`s, inside the FORM block as `box.gea` does. **A variable is valid only in its own block** (language spec section 3), so the build text cannot read these variables; a test confirms the compiler rejects that, and the Build tab hint says so.
      - The `raw` step text is inserted verbatim inside `BEGIN_BUILD`/`END_BUILD`, indented, with a comment `! Build steps (text)`; if empty, a single comment line `! (no build steps yet)` so the file still has a valid empty BUILD block.
3. Commit `Add DesignGenerator`.

## Task 3: Diagnostics mapped back to the model

**Edit:** `DesignGenerator.cs` (or a new `DesignDiagnostics.cs`). **Tests:** `DesignDiagnosticsTests.cs`.

1. Tests first (red): generate a model that has (a) a rule with an unknown name, (b) a rule that compares LENGTH with INTEGER, (c) a duplicated name that slipped past the validator by being changed after validation (construct the model directly), (d) a syntax error inside the raw build text, (e) a missing `GEA_VERSION`-type file-level error. Expect `DesignDiagnostic(Severity, Code, Message, Path?, Line)` where (a)(b) have `Path = "rules[i]"`, (c) `Path = "groups[g].params[p]"`, (d) `Path = "steps[0]"` with the line **relative to the raw text** (so the author sees the line of what they typed), (e) `Path = null`.
2. Implement: `Compile` the generated text, then for each `GeaDiagnostic` find the `LineMap` entry containing its line; for the raw build entry subtract the entry's start line. Never expose line numbers of the generated file as the main location for anything the model produced.
3. Built as `DesignDiagnostics.Check(model)` → `DesignCheckResult(Output?, Issues, Diagnostics)` (validator first; the script is generated and compiled only when the validator has no errors) and `DesignDiagnostics.Map(output, compilerMessages)`. To report the line the author typed, `LineEntry` got a `LineOffset` (blank lines the generator drops at the start of a raw step). Case (c) of the tests uses a bad `visibleWhen` instead of a duplicated name, because the validator now refuses a duplicated name before generation; the parameter-line mapping is covered with a synthetic compiler message.
4. Commit `Map compiler diagnostics to model paths`.

## Task 4: The `!@DESIGNER` block and reading it back

**New file:** `be/Cadflow.Api/Designer/DesignBlock.cs`. **Tests:** `DesignBlockTests.cs`. **Edit:** `sa-docs/gea-language-spec.md` (new section 21, see Task 9).

1. Block format (a comment block at the very end, ignored by the compiler and the worker, following section 19 of the language spec):
      ```
      ! ---------------------------------------------------------------------------
      ! CADFlow Designer: the model below lets the Designer open this file again.
      ! Do not edit the lines starting with !@ by hand; edit the file in the Designer.
      !@DESIGNER 1
      !@BODY_SHA256 <sha of the generated text without this block, normalized>
      !@MODEL <the model as one line of JSON, base64url-encoded>
      ```
      Base64url keeps the JSON on one line with no characters the lexer could treat specially. The hash uses the same normalization as `GeaExport.Sha256` (LF, exactly one trailing newline, UTF-8 without BOM).
2. Tests first (red):
      - Generate → `ReadBack` gives a model equal to the original (use value equality on the records; lists compared element by element).
      - Text edited after generation (one character changed in the body): `ReadBack` returns the model **and** `BodyChanged = true`.
      - A file with no block: `ReadBack` returns `HasModel = false` (text mode).
      - A block with a wrong version, broken base64, JSON that is not a model, or a model larger than 256 KB: `HasModel = false` with a reason; no exception.
      - A job exported with `GeaExport.Build` from a Designer script (a script ending with the Designer block): the export has both blocks; `GeaExport.StripBlock` / `ParseBlock` still work, and importing the result gives the same form (the Designer block is just comments to them). Check by an import through `JobService.Import`.
      - The compile of a file with the block is identical in form to the same file without it.
3. Implement `DesignBlock.Append(string body, DesignModel m)`, `DesignBlock.Strip(string text)`, `DesignBlock.ReadBack(string text) → (DesignModel? Model, bool BodyChanged, string? Reason)`.
4. **Change to `GeaExport.StripBlock`** (found while writing the interplay tests): it removed *every* `!@` line, so an export or import of a Designer script would have dropped the Designer block. It now keeps the three Designer lines (`!@DESIGNER`, `!@BODY_SHA256`, `!@MODEL`, column 0) and removes any other stray `!@` line as before. Consequence: a job exported from a Designer script can be opened in the Designer again.
5. `ReadBack` also treats as an edit any statement added **after** the block (blank lines and a CADFlow export block are fine, because an import drops the export block). Text typed in the Build tab cannot pose as the block: it is always indented, and only column-0 lines count; when several blocks exist the last one is used.
6. **For Task 7**: the generated file (body + block) must stay within `GeaForms.MaxScriptBytes` (256 KB), the limit of `POST /api/gea/import`. A model near the 256 KB model limit would generate a file the import refuses, so the `generate` endpoint checks the final size and reports it as an issue.
7. Commit `Add the Designer block (write and read back)`.

## Task 5: The dry run

**New file:** `be/Cadflow.Api/Designer/DesignRunner.cs`. **Tests:** `DesignRunnerTests.cs`.

1. Tests first (red), all on the box fixture and small models:
      - Values in the display unit of each field (as `GeaForms` expects), missing values → defaults; result has `form` (`GeaForms.Describe`), `valid` (no form errors), `warnings`, and `trace` (the `GeaSimulationHost` trace: the `CREATE_FEATURE` calls with evaluated inputs).
      - A rule violated by the values: `valid = false`, the error carries the rule's message and the rule's model path.
      - A rule marked `warn` violated: `valid = true` with the warning listed.
      - Hidden field (`visibleWhen` false): not in the evaluated fields' required check; its default is used (check the same behaviour as `generator.gea` rib fields).
      - A runtime error in the raw build (for example a `FAIL` statement or a builder input error) is returned as an error with the raw-text line, not as an exception.
      - Limits: the interpreter step limit (`GEA5001`) is reported as an error; a build that would exceed 1,000,000 statements does not hang the request (the existing limit stops it); the request is also bounded by a 5-second wall-clock timeout (`CancellationToken`), after which `"Dry run timed out"` is returned.
2. Implement `DesignRunner.Run(DesignModel, Dictionary<string,double>? numbers, Dictionary<string,string>? texts)`: generate → compile (errors short-circuit with their mapped diagnostics) → `GeaForms.Reject`-style checks (field count, size) → `EvaluateFormValues` → if the form is valid, `RunBuild(form, new GeaSimulationHost())`.
3. Commit `Add the Designer dry run`.

## Task 6: The builder catalog endpoint

**New file:** `be/Cadflow.Api/Designer/BuilderCatalogDto.cs`. **Test:** `BuilderCatalogTests.cs`.

1. Test first (red): `BuilderCatalogDto.List()` returns one entry per `GeaBuilderCatalog.All` item with the same name, version, target, description, and the same inputs and outputs (name, type name from `GeaUnits.TypeName`, `required`, default shown as text or null, description); input order preserved.
2. Implement (read-only projection of the existing catalog).
3. Commit `Expose the builder catalog`.

## Task 7: Endpoints

**Edit:** `be/Cadflow.Api/Program.cs` (a new section "script designer" after the `.gea` import section); register nothing new in DI unless a class needs state (these are static or constructed per request). **Test:** by `curl` against the real backend (no HTTP test infrastructure exists; the logic is covered by Tasks 1-6).

| Endpoint | Body | Returns |
|---|---|---|
| `GET /api/gea/builders` | none | `BuilderCatalogDto.List()` |
| `POST /api/gea/designer/generate` | `{ model }` | `{ gea, issues[], diagnostics[], lineMap? }`. `issues` from the validator (400 is **not** used for model problems: the UI needs them as data); `gea` is `null` when there are validator issues, otherwise the generated text (including the Designer block) plus compiler diagnostics mapped to model paths |
| `POST /api/gea/designer/parse` | `{ text }` | `{ hasModel, model?, bodyChanged, reason? }`; a text above `GeaForms.MaxScriptBytes` returns 413 |
| `POST /api/gea/designer/dryrun` | `{ model, values?, texts? }` | the `DesignRunner` result |

1. Request size: reject bodies above 512 KB (the model cap plus raw text) with 413 before deserializing into records; deserialization errors return 400 with `{ error }`.
2. Build, restart the backend (stop the process on :18080, then `dotnet run --no-build`), and check with `curl`: `GET /api/gea/builders` lists six builders; `generate` with the `box-form.design.json` fixture returns text that `POST /api/gea/import` accepts with the same form; `parse` of that text returns the model; `dryrun` with default values returns `valid: true` and a trace with the `Tank` and `Cover` calls; `dryrun` with `BX_Wall` too large returns the rule's message.
3. Commit `Add the Designer endpoints`.

## Task 8: The page

**New:** `fe/src/pages/Designer.jsx`. **Edit:** `fe/src/App.jsx` (nav entry `Designer`, route `#/designer`), `fe/src/components.jsx` (move `ScriptField` out of `ScriptOrder.jsx` into `components.jsx` as an exported component, `ScriptOrder.jsx` imports it; no behaviour change), `fe/src/lib.js` (small helpers `downloadText(name, text)`). Uses Radix Themes components already in the project; no new dependency.

1. State: the model object in React state; a draft is saved to `localStorage['cadflow.designer']` on every change (in `try/catch`, ignored if storage is unavailable) and offered on the next visit ("Continue the last draft / start new"). The model is the only thing stored.
2. A debounced (400 ms) call to `POST /api/gea/designer/generate` on every model change; the response fills the **Text** tab and the problem markers. A request that is out of date when its answer arrives is discarded.
3. Tabs:
      - **Product**: name, prefix (live check with the validator message), target shown as `PART` (fixed).
      - **Parameters**: one table per group (add/rename/delete group, move group up/down); columns name, label, type, unit, default, min, max, required, tooltip, "show only when…"; add/delete/move row; changing the type resets fields that do not apply (e.g. min/max for STRING) and shows RADIO options as a comma-separated list. A **name** is checked against the prefix as the user types. Each issue from the server is shown on the row/field named by its `path`.
      - **Rules**: table of expression, message, error/warning; variables listed above it (name, type, expression). An "available names" popover lists the parameters and variables. Diagnostics appear next to the row.
      - **Build (text)**: one monospace text area for the raw build text with a short hint ("statements go between BEGIN_BUILD and END_BUILD; the Parts tab arrives in the next version") and an "Insert the Box build as an example" button for a quick start. Diagnostics show the relative line.
      - **Text**: the generated `.gea` (read-only, monospace, the Designer block collapsed behind a "model block" toggle), **Download .gea** (`<product-name>.gea`, name sanitized), **Open .gea file…** (reads a file, calls `/parse`; with a model it loads it, with `bodyChanged` it asks "the text was edited by hand: open the model (hand edits are dropped) or open as text", without a model it shows the text only with "This file was not made with the Designer; it can be imported on the Import page but not edited here").
      - **Try it**: the form built from the dry run's `form` (using the extracted `ScriptField`); changing a value calls `/dryrun` (debounced); shows `valid`, errors with their rule messages, warnings, the trace (the builder calls) as a collapsible list; a note that **Build preview is not available in this version**. An **Order from this design** button imports the generated text through the existing `importScriptText` and goes to the Script order page.
4. Errors from the server are shown with the existing `Notice` component; nothing is silently swallowed.
5. `npx vite build` has no errors (restore `package-lock.json` if `npm install` changed it). By hand at `http://localhost:5173/#/designer` (Vite dev server): rebuild the **Box** product from scratch in the page (parameters, the seven `ASSERT`s as rules, the box BUILD pasted as text), see `valid: true`, change `Wall` to a large value and see the rule message, download the `.gea`, import it on the Script order page and order a job (the job will wait for a worker; building it needs the Windows machine); reload the page and continue the draft; open the downloaded file with **Open .gea file…** and get the same model back; edit one character of the downloaded file and open it again to see the hand-edit warning.
6. Commit `Add the Designer page`.

## Task 9: Documentation

1. `sa-docs/gea-language-spec.md`: new **section 21. The `!@DESIGNER` block** (format, normalization shared with section 19, the rule that ordinary `.gea` tools ignore it, and that `GeaExport` leaves it alone).
2. `README.md` (and `docs/` if a list of pages exists): a short paragraph for the Designer page and the three new endpoints plus `GET /api/gea/builders`.
3. `docs/superpowers/specs/2026-11-11-gea-designer-design.md`: set the status of phase 1 to "done" with the date, and record any difference between the design and what was built (for example the exact base64url format, the 5-second dry-run limit, the extra Build (text) tab).
4. Commit `Document the Designer phase 1`.

## Acceptance

- `dotnet test be/Cadflow.Api.Tests` is green, including the new test files and the existing ones (in particular `TransformerIncludeEquivalenceTests` and the shipped-script tests, unchanged).
- The model for `box.gea`'s FORM and rules generates a script whose form and validation results equal `box.gea`'s (Task 2 equivalence test).
- Every generated file compiles with no errors, and `POST /api/gea/import` accepts it; a file with the Designer block still imports, exports (`GeaExport`) and re-imports.
- A model with problems returns issues/diagnostics pointing at the right parameter, rule or raw-text line, never only a line number of the generated file.
- Reading back: generate → parse returns an equal model; a hand-edited file is detected; a hand-written file (no block) is reported as text-only and never modified.
- The Designer page can rebuild the Box FORM and rules from scratch in the browser, download the file, reopen it and order from it. **Not verified in this phase**: building the resulting job in Inventor (no Inventor on the development machine); that needs one run on the Windows worker.

## Risks specific to phase 1

1. **The golden file** fixes the exact output format; change it only on purpose.
2. **Expressions are free text**, so the first errors authors meet are expression errors. The mapped diagnostics and the "available names" list are the mitigation; a visual expression builder is not part of this phase.
3. **Hidden fields and rules**: a rule that uses a field hidden by `visibleWhen` evaluates against the field's default, as in `generator.gea`; the validator warns, but cannot decide whether the author meant it.
4. **`INCLUDE lib:` inside raw build text** works (the compiler resolves it), but the Designer does not know the library's parameters and shared variables, so name checks for them come only from the compiler. Offering libraries as a picker belongs to phase 2.
