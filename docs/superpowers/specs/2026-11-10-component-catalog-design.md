# Design: a component catalog on the web (direction A)

Date: 2026-11-10. Status: the design direction was approved in chat. Part of `docs/component-reuse-roadmap.md` (direction A); built on `INCLUDE lib:` (`2026-11-10-gea-include-design.md`).

## Goal

Users can see the components that exist (the `Scripts/lib/*.gea` libraries), download their files, and use them to create a new script or place an order right away, without writing the `INCLUDE` line themselves. The downloaded `.gea` files must **explain every step** of the script.

## Out of scope

Uploading libraries on the web (needs login, safety checks, edit/delete rights); choosing several components in one frame (there is only one library now, do it when a second one exists); Chat/MCP; exporting separate CAD files for a component (direction B).

## 1. API (server, read-only)

| Endpoint | Returns |
|---|---|
| `GET /api/components` | The list of components (see below). |
| `GET /api/components/{name}/gea` | The original library file, as a download (`attachment; filename={name}.gea`, `text/plain; charset=utf-8`). |
| `GET /api/components/{name}/starter?productName=&prefix=` | A starter script, as a download (`{name}-starter.gea`). `productName` defaults to `"New product"`, `prefix` defaults to `MY_`; an invalid value returns 400. |

Each element of the list:

```json
{
  "name": "tank-base-cover",
  "title": "Tank, base and cover",
    "summary": "Reusable component: base, tank and cover ...",
    "description": "<the whole comment paragraph at the top of the file>",
  "prefixes": ["TR_"],
  "sharedVariables": [{"name":"hTank","type":"LENGTH"}, {"name":"hCover","type":"LENGTH"}, {"name":"hTop","type":"LENGTH"}, {"name":"tbcBody","type":"REFERENCE"}],
  "parameters": [{"name":"TR_TankLength","label":"Tank length","group":"Tank, base and cover","unit":"mm","type":"Length","default":2000,"min":1000,"max":5000,"required":true}],
  "errors": []
}
```

- `title` = `PRODUCT_NAME`. `description` = the consecutive `!` comment lines at the top of the file (without the `====` rule lines and the `!`). `summary` = the first paragraph of `description` (up to a blank line or an empty `!` line).
- `parameters` come from compiling the library (`GeaScript.Compile`) and then `EvaluateFormNumbers(null)`. `sharedVariables` scans the lines `DECLARE_VARIABLE <TYPE> <name>` and `DECLARE_REFERENCE <name>` in the BUILD block of the library.
- A library that fails to compile is still in the list: `errors` holds the messages (`GEA...`), `parameters` is empty.
- The name must match `^[A-Za-z0-9_-]{1,64}$` (the same rule as `INCLUDE`); an unknown name or a missing file returns 404. Every endpoint is read-only; there is no way to write.

## 2. The starter script

Generated on the server (`ComponentStarter`), with English comments for **every part**:

```
! ===== Starter script: uses the component "<title>" (library <name>) =====
! (how to read the file: header, FORM, BUILD; a line starting with ! is a comment)
! Version of the .gea language. Required, the first statement.
GEA_VERSION 1
! Product name shown on the web.
PRODUCT_NAME "<productName>"
! Prefix for your own parameters; the component's parameters keep their own prefix (<prefixes>).
PARAM_PREFIX <prefix>
! PART = build a Part (.ipt).
TARGET PART

BEGIN_FORM
    ! Step 1: insert the parameter group of the component (lists the parameter names).
  INCLUDE lib:<name>
    ! Step 2: your own parameters. Remove the ! to enable the example:
  !   USER_INPUT_PARAM <prefix>Example LENGTH 100 mm MIN 10 MAX 500 LABEL "Example"
END_FORM

BEGIN_BUILD
    ! Step 1: insert the build steps of the component. After this line the shared variables can be used: <list>.
  INCLUDE lib:<name>
    ! Step 2: your own build steps. Example: place a block on the cover (remove the ! to enable it):
  !   CREATE_FEATURE builder:Block ...
END_BUILD
```

The skeleton must **compile without errors** and be accepted by `POST /api/gea/import` without any edit. The example in the comments uses the builder syntax that exists (`Block`), and the variable list comes from `sharedVariables`. `productName` goes into a string, so `"`, `\` and line breaks are blocked; `prefix` matches `^[A-Za-z][A-Za-z0-9]{0,15}_$`.

## 3. Explaining every step in the `.gea` file

- `tank-base-cover.gea` is re-annotated: every step (declare variables, compute elevations, build the base, build the tank, build the cover, set names and attributes) has a comment that says what it means and gives the formula. Comments only: the equivalence tests against the original `transformer.gea` must still pass.
- **Convention** (recorded in `sa-docs/gea-language-spec.md` section 20): the first paragraph of the comment at the top of the file is a short description shown on the card; every **statement paragraph** (consecutive lines with no blank line between them) in the FORM/BUILD block of a library must have at least one comment line. A test applies this convention to every file in `Scripts/lib`, so a library added later must also explain itself.
- Comments are in English.

## 4. User interface (`fe/`)

- Tab **Components** (`#/components`) trong `App.jsx`; trang `pages/Components.jsx`.
- One card per component: title, description (the summary; expandable to see the whole `description`), shared variables, a parameter table grouped by group (collapsible), the two `INCLUDE` lines to copy, an error notice if `errors` is not empty.
- Three buttons: **Download library (.gea)**; **Download starter script (.gea)**; **Order this component**. The third fetches the starter script (`/starter`), sends it to the existing `/api/gea/import`, stores `sessionStorage['cadflow.import']` like `ImportGeaButton`, and goes to `#/import/<scriptId>`. The shared part is extracted into the function `importScriptText(text, fileName)` in `lib.js` so both places use one path.
- The loading state and errors (network, 400/404) show with a `Callout` like the other pages.

## 5. Code

`be/Cadflow.Api/Components.cs` (`ComponentCatalog`, `ComponentStarter`), `GeaLibraries` gets `Names()`; the endpoints in `Program.cs`; `fe/src/pages/Components.jsx`; edits to `App.jsx`, `lib.js`, `components.jsx`, `app.css`.

## 6. Testing

`Cadflow.Api.Tests`:
- The list contains `tank-base-cover` with 7 parameters, prefix `TR_`, variables `hTank`/`hCover`/`hTop`/`tbcBody`, a non-empty `summary`, empty `errors`.
- A broken library (a temporary library folder) is still in the list with `errors`; `description`/`summary` are extracted correctly.
- The downloaded file is byte-identical to the original; an unknown name (`..`, `a/b`, nonexistent) is 404.
- The starter script: compiles without errors; import through `JobService.Import` returns 200 and the form has the component parameters; a custom product name and prefix are applied; invalid input is rejected; every part has a comment (header, FORM, BUILD, the place to add parameters).
- The comment convention applies to every `Scripts/lib/*.gea` file.
- The equivalence test against `transformer.gea` still passes.
- UI: no automated test in the repo; check with `vite build` and by hand (listing, three buttons, a trial order).

## Risks

- The user API has no authentication yet; the new endpoints only read the libraries that ship with the server, so the risk is low.
- Scanning `DECLARE_*` with a regex is only correct for declarations written one per line, as they are now.
- There is only one library now, so the UI has not been tried with many components; the list is still correct for any count.
