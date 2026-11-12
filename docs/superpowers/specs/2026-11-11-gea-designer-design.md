# Design: a visual designer for `.gea` scripts (the "Product Designer")

Date: 2026-11-11. Status: phase 1 done on 2026-11-11 (see "Phase 1 as built" at the end); later phases not started. Decisions taken in chat on 2026-11-11: (1) designer-managed files are editable in the UI, hand-written files open as text only (section 1); (2) the `DRAWING_LEVEL` statement will be built (section 6); (3) the goal is the most general machines possible, so the builder catalog must grow from product-specific builders to general modelling operations (section 13, a separate design). Builds on `INCLUDE lib:` (`2026-11-10-gea-include-design.md`) and the component catalog (`2026-11-10-component-catalog-design.md`). Language reference: `sa-docs/gea-language-spec.md`.

## Goal

A new machine (for example a fan) should be created by someone who understands the machine, **without knowing the `.gea` syntax and without writing C#**. Today every product is a `.gea` file written by hand (`generator.gea` is 341 lines, `transformer-hv.gea` 560). The web only *runs* scripts (`ScriptOrder.jsx`) and the add-in only *edits text* (`ScriptPanel`). Nothing helps to *author* one.

The Designer is a web page where the author:

1. declares the input parameters (the order form),
2. declares the rules between them (validation),
3. lists the parts of the machine, choosing a builder for each and filling its inputs,
4. sees errors and a preview straight away,
5. saves the result as a normal `.gea` file, which then runs through the existing import → order → worker path unchanged.

## Out of scope (first version)

- New builders. The Designer can only offer what `GeaBuilderCatalog` offers (Tank, Cover, Block, Cylinder, Bushing, Radiator). Because the goal is general machines, the catalog must be extended with general modelling operations; that is a separate design (section 13), and it is the main limit on what the Designer can express.
- `TARGET ASSEMBLY`, `TABLES`, `LOOKUP`, `BEGIN_CATCH_ERROR`: not modelled as steps; they can only live in a **Raw** step (section 3).
- Publishing a script as a named product with a Config tab (roadmap, phase 4 below).
- Login, per-user ownership, sharing, version history of designs (needs the PLM/auth work first).
- Editing a model graphically in 3D (dragging parts). Parts are placed by numbers and expressions.

## 1. Principle: `.gea` stays the single source of truth

The server, the worker and the add-in keep consuming plain `.gea`. The Designer is only a **generator of `.gea`** plus a structured editor for what it generated. Nothing downstream changes.

**Why not parse any `.gea` back into the UI (true round-trip):** `GeaLexer` drops comments (`!` and `/* */`) before parsing, so an AST-based regeneration would silently delete every comment of a hand-written file, and comments are most of the documentation in `generator.gea`. Therefore:

- The Designer keeps its own **design model** (JSON, section 2) and generates the `.gea` from it deterministically.
- The model is stored inside the generated file as a comment block, following the existing `!@` convention (`sa-docs/gea-language-spec.md` section 19), so a file is self-contained: open it again and the Designer restores the model exactly.
- A `.gea` **without** a `!@DESIGNER` block (hand-written, or exported by CADFlow) opens in the **Text** tab only, with a one-click "Convert to designer model" that does a best-effort import (section 5) and warns about what will be lost. Hand-written files are never modified silently.

## 2. The design model

```json
{
  "version": 1,
  "product": { "name": "Axial fan", "prefix": "FN_", "target": "PART" },
  "groups": [
    { "title": "Casing", "params": [
      { "name": "FN_CasingDiameter", "type": "LENGTH", "default": 1200, "unit": "mm",
        "min": 400, "max": 3000, "required": true, "label": "Casing diameter",
        "tooltip": "Inner diameter of the cylindrical casing",
        "visibleWhen": null }
    ]}
  ],
  "rules":  [ { "expr": "FN_HubDiameter < FN_CasingDiameter / 2", "message": "Hub must be smaller than half the casing.", "warn": false } ],
  "vars":   [ { "name": "axisH", "type": "LENGTH", "expr": "FN_BaseHeight + FN_CasingDiameter / 2" } ],
  "steps":  [
    { "kind": "feature", "builder": "Cylinder", "out": "casing", "refName": "FN_Casing*", "role": "CASING",
      "inputs": { "Diameter": "FN_CasingDiameter", "Length": "FN_CasingLength", "Axis": "\"X\"", "Elevation": "axisH" } },
    { "kind": "repeat", "var": "i", "from": "0", "to": "FN_BladeCount - 1", "steps": [ { "kind": "feature", "...": "..." } ] },
    { "kind": "raw", "text": "REGEN_MDL" }
  ],
  "drawing": { "levels": ["0", "FN_BaseHeight", "axisH", "axisH + FN_CasingDiameter / 2"] }
}
```

- Parameter `type` is one of the kinds the parser accepts: `LENGTH`, `ANGLE`, `INTEGER`, `DOUBLE`, `STRING` (`USER_INPUT_PARAM`), plus `CHECKBOX` and `RADIO` with `options` (`CHECKBOX_PARAM`, `RADIOBUTTON_PARAM`). `GeaParser` rejects BOOL in `USER_INPUT_PARAM` (error `GEA1003`), so the UI must not offer it there.
- **Expressions are plain strings in `.gea` expression syntax** (names, numbers with units, `MIN`, `MAX`, `STR`, …, section 10 of the language spec). The first version does not build an expression tree in the UI; it offers a text box with name auto-completion and instant checking (section 4). This keeps the model small and makes the generated file honest.
- `steps` nest only for `repeat` (a `FOR` loop). `raw` carries verbatim lines for anything the UI does not model, so no valid construct is ever unreachable.
- `drawing.levels` is the list of elevations the drawing uses to write the dimension string. Today this is C# per product (`ScriptedProduct.Levels`) and empty for `imported` jobs, so scripts from the Designer would otherwise get a drawing without elevation dimensions. See section 6.

## 3. Mapping to `.gea`

The generator emits the file in a fixed order, so output is stable and diff-friendly:

| Model | Generated `.gea` |
|---|---|
| `product` | `GEA_VERSION 1`, `PRODUCT_NAME`, `PARAM_PREFIX`, `TARGET` |
| `groups[].params[]` | `BEGIN_FORM`, `BEGIN_GROUP "title"`, `USER_INPUT_PARAM name TYPE default unit MIN MAX REQUIRED LABEL "…"`, `END_GROUP`, `END_FORM` |
| `visibleWhen` | wraps the param in `IF <expr> … END_IF` (a hidden field has no value, as in `generator.gea` rib fields) |
| `rules[]` | `ASSERT <expr> "<message>"` (`warn: true` → the warning form of `AssertStmt`) |
| `vars[]` | `DECLARE_VARIABLE TYPE name` + `name = <expr>` inside FORM, before the `ASSERT`s. A variable is valid only in its own block (language spec section 3), so rules can use it but the build text cannot |
| `steps[kind=feature]` | `DECLARE_REFERENCE out`, `CREATE_FEATURE builder:X`, one `FEATURE_IN "Port" <expr>` per filled input, `FEATURE_OUT out "Body"`, `END_CREATE_FEATURE`, then `SET_REF_NAME` / `SET_REF_ATTR out "ROLE" …` |
| `steps[kind=repeat]` | `FOR var FROM a TO b … END_FOR` |
| `steps[kind=raw]` | the text, unchanged |
| progress | `PROCESSING_BOX_START` / `SET_STATE` / `END` generated from the step titles so the web job page shows progress |
| whole model | JSON appended as `!@DESIGNER 1` / `!@MODEL <base64 or one JSON line>` at the end of the file, after the generated body |
| the file | Every generated section is preceded by a short English comment, in the style of `ComponentStarter` (`be/Cadflow.Api/Components.cs`), so the output is readable if someone opens it in a text editor |

The generated file must pass `GeaScript.Compile` with no errors. This is enforced by tests (section 9) and by the Save button (it is refused while the model has errors).

## 4. Builder inputs come from the manifests

The "Parts" tab never hard-codes a builder. It asks the server for the catalog:

| Endpoint | Returns |
|---|---|
| `GET /api/gea/builders` | Every `BuilderManifest`: `name`, `description`, `inputs[]` (`name`, `type`, `required`, `default`, `description`), `outputs[]`. Read from `GeaBuilderCatalog.All`, which the server already compiles (`be/Cadflow.Api.csproj` links `Gea/*.cs`). |

For each part the UI shows one row per input port: label = port name, hint = port description, required ports marked, optional ports collapsed under "Advanced" and left out of the generated file when empty (so `Default` applies). A port of type `Reference` (for example `Cover.Tank`) is a drop-down of the `out` names defined by earlier steps. Adding a builder to the catalog in C# makes it appear in the Designer with no UI change.

## 5. Checking while the author types (no Inventor needed)

| Endpoint | Does |
|---|---|
| `POST /api/gea/designer/generate` | Body: the model. Returns `{ gea, diagnostics[] }`: generated text plus the `GeaChecker` diagnostics (code, severity, line, message). |
| `POST /api/gea/designer/parse` | Body: `.gea` text. If it has a `!@DESIGNER` block returns the model; otherwise a best-effort model from the AST (parameters, `ASSERT`, `CREATE_FEATURE`, `FOR`; everything else becomes `raw`) with a list of "what was lost" (all comments, hand formatting). The UI shows that list and requires confirmation. |
| `POST /api/gea/designer/dryrun` | Body: model + parameter values. Runs the interpreter without Inventor (the language has a dry-run Host, `GeaHost`), returns the validation result and the list of builders that would be called with their evaluated inputs. This is what lets an author test rules and loops on a machine with no Inventor. |

Diagnostics are mapped back from `.gea` line numbers to the model element that produced them (the generator keeps a line → element table), so the error appears next to the right field or part, not as "line 143".

The limits from `sa-docs/gea-language-spec.md` section 19.1 apply: file at most 256 KB, at most 200 form fields, `GeaInterpreter.MaxSteps` = 1,000,000 statements.

## 6. Drawing levels for scripts that are not built-in products

Now: `InventorJobExecutor` takes `levels` from `ScriptedProduct.Levels` (a C# lambda per product) and uses `Array.Empty<double>()` for `imported`. Proposal: a script may declare them itself, with a new optional statement inside BUILD, for example `DRAWING_LEVEL <expr>` (repeatable), and the executor collects them while running the script. The Designer's `drawing.levels` generates these lines. This is a **language change** (touches `GeaParser`, `GeaInterpreter`, the language spec, the Host interface) and is also what lets built-in products drop their C# `Levels` later. **Decided: it will be built**, in phase 3 below; until then Designer scripts get a drawing without elevation dimensions, which the UI states plainly. Details to settle when that phase starts: the statement is allowed only in BUILD; the values are collected in order and sorted/de-duplicated by the executor; a script with no `DRAWING_LEVEL` keeps today's behaviour (built-in products use their C# `Levels`, `imported` has none); the add-in Host and the server dry-run Host both need the new statement, and the Host part cannot be run on the development machine (no Inventor).

## 7. The page

New page **Designer** in the navigation, next to Components. Layout: left column = steps, right = live result.

| Tab | Contents |
|---|---|
| 1. Product | Name, parameter prefix (same validation as `ComponentStarter.Validate`: starts with a letter, letters/digits, ends with `_`). |
| 2. Parameters | Table grouped by `BEGIN_GROUP`: name, label, type, unit, default, min, max, required, tooltip, "show only when…". Rows can be reordered and moved between groups. A live form preview on the right renders the same form `ScriptOrder.jsx` would. |
| 3. Rules | One row per rule: expression, message, error/warning. Name auto-complete from the parameters and variables. |
| 4. Parts | Ordered list of steps: **Part** (choose builder, fill inputs), **Repeat** (variable, from, to, contains steps), **Variable** (name, type, expression), **Raw** (text). Drag to reorder. |
| 5. Text | The generated `.gea`, read-only; the `!@DESIGNER` block shown collapsed. A **Download .gea** button. |
| 6. Try it | Fill the form from section 2, press **Check** (dry-run, instant, no Inventor) or **Build preview** (submits a `preview` job: `InventorJobExecutor` already supports `kind=preview`; needs a worker with Inventor). |

Saving in the first version is **local**: Download .gea, and later Import it on the existing Script order page. Persistent storage on the server comes with phase 4.

## 8. AI-assisted first draft (optional, after phase 2)

The web already has Chat (`Chat.jsx`, `docs/chat-mcp.md`). A "Describe the machine" box sends the text to the model with the builder manifests and the design-model JSON schema as context; the answer is a **design model**, not raw `.gea`. The model then goes through the same `generate` + `GeaChecker` path as everything else and opens in the Designer for the author to review and edit. Rationale: a JSON model validated against a schema is much easier to reject and repair than free `.gea` text, and the author sees the result in the structured editor. AI output is never saved or submitted without the author pressing a button.

## 9. Testing

`be/Cadflow.Api.Tests`:
- Generator: every shipped script that has a design model (the fixtures written for this feature) generates text that `GeaScript.Compile` accepts with zero errors.
- Golden file: a fixed model generates a fixed `.gea` (guards the stable ordering).
- Model ↔ file: generate, parse the `!@DESIGNER` block back, get an equal model.
- `parse` on a hand-written file without the block (`box.gea`): returns a best-effort model, reports "comments lost", and the regenerated file still compiles.
- Diagnostics mapping: an invalid expression in a rule is reported against that rule, not a line number.
- A `Reference` input pointing at an undefined step is reported.
- Limits: 201 parameters refused; a model that would generate a file over 256 KB refused.
- `GET /api/gea/builders` lists exactly `GeaBuilderCatalog.All`.

Front end: manual check of the five tabs against the shipped products. This development machine has no Inventor, so **Build preview must be verified on the Windows machine with Inventor 2027**; the dry-run path can be fully tested here.

## 10. Security

- All endpoints compile and dry-run server-side only; the language has no file, network or process statements (language spec 19.1), and `raw` text goes through the same compiler and limits as any imported script.
- Names, labels and messages go into quoted strings and identifiers of the generated file: validate them like `ComponentStarter.Validate` (no quotes, backslashes or control characters in names and labels; identifiers match `[A-Za-z][A-Za-z0-9_]*`) and escape strings, so a model cannot inject statements.
- The model JSON is capped in size and nesting depth before it is processed.

## 11. Phases

| Phase | Content | Depends on |
|---|---|---|
| 1 | Model, generator, `GET /builders`, `generate`, `dryrun`; tabs Product / Parameters / Rules / Text; download. Removes the need to write `FORM` and `ASSERT` by hand. | nothing |
| 2 | Tab Parts (builders from the manifests, Repeat, Variable, Raw); `parse` for hand-written files; Try it → Check. | phase 1 |
| 3 | `DRAWING_LEVEL` statement and executor support (decided); Build preview; AI first draft. | phase 2; Windows machine for the Inventor side |
| parallel | General builders (section 13): own spec and plan; most useful once phase 2 exists, but independent of phases 1-2. | Windows machine with Inventor 2027 |
| 4 | Publish a design as a named product (stored on the server, listed in `/api/products`, Config tab, no per-product C#). Needs a decision on how `Products` (currently C# per product) becomes data-driven. | phase 3 and the Config/Products design |

## 12. Risks and open decisions

1. **The builder catalog is the ceiling.** With six builders the Designer can express boxes, cylinders and tanks, not fan blades. Authoring friendliness and builder richness must grow together (section 13).
2. **Round-trip is deliberately limited** (section 1). Decided: only designer-managed files are editable in the UI; hand-written files open as text. If this is ever reversed, it needs a comment-preserving parser (a lossless syntax tree), a much larger change to `GeaLexer`/`GeaParser`.
3. **Expressions are text** in v1. Authors who do not know `MIN(...)` or unit syntax will still hit errors. Mitigation: auto-complete, instant diagnostics, a one-line help per function; a visual expression builder is a possible later step.
4. **Embedding the model in the `.gea`** makes the file longer and means a hand-edit of the generated body makes the model and the text disagree. The `!@SCRIPT_SHA256` idea from section 19 can be reused: store the hash of the generated body; on open, a mismatch means the text was edited by hand, and the Designer warns and offers "keep text only" or "discard the edit".
5. **`DRAWING_LEVEL` is a language change** that must stay compatible with the add-in Host, which cannot be run on the development machine.
6. Who may publish a product in phase 4 is not decided (no login yet).

## 13. Towards general machines: the builder catalog (separate design)

The goal "as general as possible" changes what the catalog should contain. Today's builders are **product-shaped** (Tank, Cover, Bushing, Radiator are transformer parts). Every new machine type would need its own such builder written in C#, which brings back the problem this work is meant to remove. The general answer is to offer **modelling operations** that any machine can be composed from, and keep product-shaped builders only as optional shortcuts built from them.

Proposed families (each is a builder with a manifest, in the same format as today, so the Designer shows them with no UI change):

| Family | Examples | What it unlocks |
|---|---|---|
| Sketch profile + extrude | polygon / rectangle / circle / slot profile, extrude, cut | brackets, flanges, plates, ducts, arbitrary prismatic parts |
| Revolve | profile revolved about an axis | hubs, casings, shafts, pulleys, bushings, nozzles, volutes of simple section |
| Sweep / loft | profile along a path; loft between sections | pipes, bends, transitions, blade-like shapes |
| Patterns | linear, circular (count, angle, axis) of a body or feature | fan blades, bolt circles, fins, ribs (replaces many `FOR` loops with real feature patterns) |
| Placement | move / rotate / mirror a body | anything that is not axis-aligned |
| Boolean | combine (join / cut / intersect) bodies | hollow casings, openings, holes |
| Finishing | fillet, chamfer, hole, shell | realistic edges, bores |

Notes from what exists today:
- All of these have counterparts in the Inventor API (extrude, revolve, sweep, loft, rectangular and circular pattern, combine, fillet, hole features), so this is feasible, but **none of it can be run on the development machine** (no Inventor): each builder needs a test on Windows with Inventor 2027, as with all add-in work in this repository.
- `GeaBuilderCatalog` ports are typed (`Length`, `Reference`, …); sketch profiles need a point-list / profile type, a language-level addition to `GeaType` and to the manifest format.
- Bodies built from a sketch need an orientation convention beyond today's "X / D / Elevation" system (for example a work plane per body). That convention must be decided before the first builder is written, or every later builder will be inconsistent.
- A twisted fan blade is then a loft between a few airfoil sections, placed by a circular pattern; a centrifugal housing is a sweep or revolve with a boolean for the outlet. Neither is a special builder.

This section is intentionally a direction, not a design. **Next document to write:** `2026-11-1x-gea-general-builders-design.md` (orientation convention, profile type, the first 4-5 builders, test plan on Windows). It is independent of the Designer phases 1-2 and can start in parallel; the Designer picks the new builders up automatically through `GET /api/gea/builders`.

## Phase 1 as built (2026-11-11)

Plan: `docs/superpowers/plans/2026-11-11-gea-designer-phase1.md`. Differences from the design above:
- **`parse` is in phase 1** (the design had it in phase 2): the Open .gea file button needs it. It reads only files with a `!@DESIGNER` block; a hand-written file opens as text.
- **Extra tab Build (text)**: the Parts tab does not exist yet, so the build is raw text (one or more raw steps, only `kind: "raw"` is accepted). The compiler's line numbers for it are the lines the author typed.
- **Block format**: three lines, `!@DESIGNER 1`, `!@BODY_SHA256 <hex>`, `!@MODEL <base64url, no padding, UTF-8 JSON>`; details in the language spec section 21. `GeaExport.StripBlock` was changed to keep these lines.
- **Dry run**: `DesignRunner`; stages `design`, `compile`, `form`, `build`, `timeout`. The wall-clock limit is 5 seconds; the interpreter cannot be cancelled, so a runaway thread stops at its own limit of 1,000,000 statements. The simulation host records builder calls and makes no geometry (no Build preview yet).
- **Variables** declared in the design are valid in FORM only (a variable lives in the block that declares it); a test confirms the compiler rejects their use in BUILD.
- **Limits**: model 256 KB, 200 parameters, 50 groups, 100 rules, 100 variables; request body 512 KB; the generated file with its block at most 256 KB (reported as an issue by `generate`).
- Not checked by an automated test: the page itself (checked by hand in the browser; downloading, opening a file and ordering need a manual run).
