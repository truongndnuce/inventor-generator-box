# Implementation plan: a component catalog on the web (direction A)

Design: `docs/superpowers/specs/2026-11-10-component-catalog-design.md`. Each task ends with a commit; tests run with `dotnet test be/Cadflow.Api.Tests` (needs a local PostgreSQL, see `docs/database.md`). The UI has no automated tests in the repo: check with `npx vite build` (in `fe/`) and by hand. Do not touch `fe/src/pages/Chat.jsx` (it is being edited separately).

## Task 1: Explain every step in `tank-base-cover.gea` + the convention test

**File:** `InventorAddin/InventorAddin.Core/Scripts/lib/tank-base-cover.gea`. **New test:** `be/Cadflow.Api.Tests/ComponentCommentsTests.cs`.

1. Test first (red): for every `Scripts/lib/*.gea` file in the test output folder:
      - Cut out the `BEGIN_FORM...END_FORM` and `BEGIN_BUILD...END_BUILD` blocks; inside each block split into **statement paragraphs** (consecutive lines with no blank line) and assert that each paragraph has at least one comment line (`!` at the start of the line after trimming).
      - The comment at the top of the file (the `!` lines before `GEA_VERSION`) has at least one description paragraph (a line that is not a `====` rule and not empty).
      - The error message names the file and the first line of the paragraph that lacks a comment.
2. Re-annotate the library (English, one "Step N: ..." line per statement paragraph with the meaning/formula): declaring the elevation variables and the reference variable; `hTank = TR_BaseThickness` (the tank bottom sits on the base), `hCover` (cover bottom = base + tank height), `hTop` (cover top); each `CREATE_FEATURE builder:Block` (the size, why it overhangs by `TR_BaseOverhang`/`TR_CoverOverhang`, `Elevation`); `SET_REF_NAME`, `SET_REF_ATTR ... "ROLE"`, `SET_APPEARANCE`. The FORM paragraph (the parameter group) also has comments saying what each parameter is. **Only add comments**, do not change statements.
3. Run all the tests: the convention test is green, `TransformerIncludeEquivalenceTests` is still green (no behavior change).
4. Commit `Explain each step in tank-base-cover.gea; enforce comment convention for libraries`.

## Task 2: `ComponentCatalog` (list + details)

**New file:** `be/Cadflow.Api/Components.cs`. **Edit:** `be/Cadflow.Api/GeaLibraries.cs` (add `Names()`: the names of `*.gea` files in `Scripts/lib` matching `^[A-Za-z0-9_-]{1,64}$`, sorted). **New test:** `ComponentCatalogTests.cs`.

1. Test first (red), using a temporary library folder to build fake libraries (`ComponentCatalog` takes a `Func<IEnumerable<string>> names` and a `Func<string,string?> read`, defaulting to `GeaLibraries`):
      - The real library `tank-base-cover`: `title` = `Tank, base and cover`, `prefixes` = `["TR_"]`, 7 parameters (`TR_TankLength` ... `TR_CoverOverhang`) with a label, group, unit `mm`, default, min, max, `required`; `sharedVariables` has `hTank`, `hCover`, `hTop` (LENGTH) and `tbcBody` (REFERENCE); `summary` is not empty; `errors` is empty.
      - `description`: the `!` lines at the top of the file without the `!` and the rule lines; `summary` = the first paragraph (up to an empty `!` line).
      - A broken library (a syntax error): still in the list, `errors` has a `GEA...` code, `parameters` is empty.
      - `Get("..")`, `Get("a/b")`, `Get("missing")` return null.
      - Sorted by name; the list is empty when there are no libraries.
2. Implement `ComponentCatalog.List()`, `Get(name)`, and the DTO records `ComponentInfo`, `ComponentParam`, `ComponentVar`. Compile with `GeaScript.Compile(text, GeaLibraries.Resolve, name + ".gea")`; the parameters come from `EvaluateFormNumbers(null).Fields` (Name, Label, Group, Unit, Type, Min, Max, Required, the default value taken from `Values`). Scan `sharedVariables` with a regex over the BUILD block of the library.
3. Commit `Add ComponentCatalog`.

## Task 3: `ComponentStarter` (an annotated starter script)

**Edit:** `be/Cadflow.Api/Components.cs`. **New test:** `ComponentStarterTests.cs`.

1. Test first (red):
      - `Build("tank-base-cover")` (default): compiles without errors (`GeaScript.Compile(text, GeaLibraries.Resolve)`), has `INCLUDE lib:tank-base-cover` exactly twice (FORM and BUILD), `PARAM_PREFIX MY_`, `PRODUCT_NAME "New product"`.
      - `Build(name, productName: "Reactor", prefix: "RX_")` is applied correctly; the form of the compiled copy has the parameter `TR_TankLength`.
      - `JobService.Import(starter)` returns 200, the `form` has `TR_TankLength`; creating a job from the `scriptId` succeeds (using `TestDb`).
      - Rejected: a `productName` with `"`, `\` or a line break or longer than 80 characters; a `prefix` that does not match `^[A-Za-z][A-Za-z0-9]{0,15}_$`; a missing library (returns an error, does not throw).
      - Every part is commented: it contains explanation lines for the header (`GEA_VERSION`, `PRODUCT_NAME`, `PARAM_PREFIX`, `TARGET`), FORM, BUILD, the place to add your own parameters, and lists the shared variables of the library; the "every statement paragraph has a comment" convention of Task 1 also holds for the starter script.
2. Implement following the template in section 2 of the spec: a comment at the top of the file, each header statement with its own comment, one comment per step in FORM/BUILD, the `USER_INPUT_PARAM` and `CREATE_FEATURE builder:Block` examples commented out (using exactly the syntax of `transformer.gea`, placing the block on `hTop`).
3. Commit `Add ComponentStarter`.

## Task 4: Endpoint

**Edit:** `be/Cadflow.Api/Program.cs` (register `ComponentCatalog` as a singleton). **Test:** try with `curl` on the real backend (there is no HTTP test infrastructure in the repo; the logic was tested in Tasks 2-3).

1. `GET /api/components` → `ComponentCatalog.List()`; `GET /api/components/{name}/gea` → `Results.File(bytes, "text/plain; charset=utf-8", fileDownloadName: name + ".gea")`, 404 if `Get` is null; `GET /api/components/{name}/starter` (query `productName`, `prefix`) → the file `name-starter.gea`, 400 if the input is invalid, 404 if the library is missing.
2. Build, restart the backend (`kill` the process listening on :18080, then `dotnet run --no-build`), check with `curl`: the list, download `gea` (compare with the original file using `diff`), `starter` (then `POST /api/gea/import` with that content), 404 for `..` and unknown names, 400 for a bad prefix.
3. Commit `Add component catalog endpoints`.

## Task 5: User interface

**Edit:** `fe/src/lib.js` (add `importScriptText(text, fileName)`), `fe/src/components.jsx` (`ImportGeaButton` uses the new function, behavior unchanged), `fe/src/App.jsx` (the route `#/components`, a NAV entry `Components`), `fe/src/app.css`. **New:** `fe/src/pages/Components.jsx`.

1. `importScriptText`: `postJson('/api/gea/import', { text })`, store `sessionStorage['cadflow.import']` (`scriptId`, `values`, `texts`, `warnings`, `hasValues`, `fileName`), `go('#/import/<id>')`; errors are rethrown as they are now (with `e.body.diagnostics`).
2. `Components.jsx`: `api('/api/components')`, one card per component (title, summary, a button that expands the description, shared variables, the parameter table by group inside `<details>`, a block with the two `INCLUDE` lines and a Copy button, a `Callout` for `errors`), three buttons: two download links (`<a href download>` to `/gea` and `/starter`) and the **Order this component** button (`fetch` the starter → `importScriptText`). Loading/error states with `Callout`.
3. `npx vite build` has no errors (restore `package-lock.json` if `npm install` changed it). Try by hand on `http://localhost:5173/#/components` (the Vite dev server is running): see the `tank-base-cover` card, download both files, the Order button opens the form `#/import/...` with the Tank, base and cover fields. If browser automation is available, take a screenshot; if not, say clearly which parts were not seen by eye.
4. Commit `Add Components page`.

## Task 6: Documentation

- `sa-docs/gea-language-spec.md` section 20: add the sub-sections "Component catalog on the web" (endpoints, buttons, starter script) and the **library comment convention** (the first paragraph is the description, every statement paragraph has a comment, with a mandatory test).
- `docs/component-reuse-roadmap.md`: mark direction A as done, and note what differs from the design (if anything).
- `README.md`: one line about the Components tab.
- Run all the tests, commit `Document component catalog`.

## Acceptance

- `dotnet test be/Cadflow.Api.Tests` is all green (the 80 existing tests + the new tests).
- On the real backend: `/api/components` returns `tank-base-cover`; the downloaded file is readable and every step is explained; the starter script can be imported and ordered (job `queued`).
- `git status` is clean except `Chat.jsx` (the user's separate edit); do not commit `.env` or build files.
