# Roadmap: reusing and exporting components

Status as of 2026-11-10. `INCLUDE lib:` is done (see `superpowers/specs/2026-11-10-gea-include-design.md` and `sa-docs/gea-language-spec.md` section 20): a script reuses the parameter group and the build steps of the library `Scripts/lib/<name>.gea` (currently `tank-base-cover`). This document records three next directions. **This is a roadmap, not a detailed design**: each direction needs its own design (spec, then plan) before work starts.

## What to know about the current state

- A job builds **one Part** and exports `<jobId>.ipt`, `<jobId>.stp`, the `.png` images and the `.pdf` (`InventorJobExecutor`). Base, Tank and Cover are three solid bodies in the same Part, named `TR_Base`, `TR_Tank`, `TR_Cover` (`SET_REF_NAME`), with the attribute `ROLE` = `BASE`/`TANK`/`COVER`.
- The **Export .gea** button on the job page and the Dashboard exports **the whole script** of the job with its values (`GeaExport`), the INCLUDE-expanded copy. There is no way to export a single component on its own.
- The web has no screen that lists the libraries; to use a library you have to write the `INCLUDE` line yourself.
- `ADD_COMPONENT`, `BEGIN_ASSEMBLE`/`END_ASSEMBLE`, `MATE`, `INSERT`, `EXPORT_STEP` are statements that are **reserved (P2) but not built**. `TARGET ASSEMBLY` can only be dry-run, the Inventor Host does not support it (`GEA2020` warning). The server accepts only `TARGET PART`.
- The current development machine **has no Inventor**: add-in code can only be written, not run. Every item below that involves the add-in must be tested on a Windows machine with Inventor 2027.

## Direction A: a component catalog on the web (download `.gea` files) — DONE (2026-11-10)

> Done following `superpowers/specs/2026-11-10-component-catalog-design.md`: the Components tab, three buttons (order the component itself, download a starter script, download the library), libraries annotated step by step with a test that enforces the convention. Different from the sketch below: there is no zip for nested libraries, and the description comes from the comment at the top of the file. The rest of this section is the original sketch.

**Goal**: see the components that exist and download the `.gea` file of each one, to use in your own script.

- API: `GET /api/components` (lists `Scripts/lib`: name, description, prefix, the parameter list of the FORM block, the variables the parent script may use) and `GET /api/components/{name}/gea` (downloads the original library file, nested `INCLUDE` lines kept as they are).
- The component description comes from `PRODUCT_NAME` and the comment at the top of the file; the parameters come from compiling the library (`GeaScript` already exists).
- Web: a **Components** page in the navigation bar, one card per component (name, description, parameter table, a **Download .gea** button, the two `INCLUDE` lines to copy).
- An option for later: download the nested libraries together as one zip file.
- Risk: almost none (it only reads files). No Inventor needed.
- Testing: `Cadflow.Api.Tests` (lists correctly, an invalid name returns 404, nothing is read outside `Scripts/lib`).

## Direction B: export a separate CAD file for each component

**Goal**: one job (for example a transformer) also returns separate `Tank.ipt`/`.stp`, `Base.ipt`/`.stp`, `Cover.ipt`/`.stp` files, to use elsewhere.

- How to declare it (to settle during design): a statement in the BUILD of the library, for example `EXPORT_COMPONENT tbcBody "Tank"`, or a declaration in the library header listing the bodies to export. The statement is also needed so the script author can choose which component to export.
- Execution (add-in): create a new Part holding only that body (derive or copy the body), save `.ipt`, export `.stp`; add them to the artifact list of the job with a new `kind` (`component-ipt`, `component-step`) and a name with the component prefix. The server and R2 already accept artifacts by `name`/`kind`, so little needs to change; the job page needs to group the files by component.
- Simulation Host: record the command trace (so it can be tested on the server like `GeaSimulationHost`).
- Parameters: the exported component is **the result of this build** (dimensions fixed per job), it has no form of its own.
- Open questions: export by default or switch it on with a parameter; one body or a group of bodies per file; the file name; whether to include separate images; the effect on build time (each component adds one save and one STEP export).
- Risk: the add-in and the Inventor API (copying a body to another Part keeps its position or moves it to the origin). Cannot be tried on this machine.

## Direction C: insert built Parts into an assembly

**Goal**: a script builds an assembly, inserting Parts (for example the built Tank, base and cover) and positioning them with mates.

- Language: build the reserved P2 statements: `TARGET ASSEMBLY`, `ADD_COMPONENT`, `BEGIN_ASSEMBLE`/`END_ASSEMBLE` with `MATE`, `FLUSH`, `INSERT`. A detailed spec is needed in `sa-docs/gea-language-spec.md` (section 11 currently only holds a place) and in the manifest table.
- Source of the Part: (1) a Part this script builds itself with `INCLUDE` and then saves separately; (2) a Part that already exists from an earlier job (`jobId` + component name, thanks to direction B); (3) an `.ipt` file in the library. Start with (1) and (2).
- Host: an Inventor Host for `ASSEMBLY` (creates an `.iam`, `Occurrences.AddByComponentName`, mate constraints), a simulation host for tests. The server currently accepts only `TARGET PART`: it must be opened to ASSEMBLY, the limits (size, statement count) recalculated, and a way defined for a job to get its input Part.
- Open questions: how to identify a component in the assembly (naming, attributes); what to do when the source Part was deleted by the file retention policy (`Retention:Days`); whether mates are enough for a rectangular box body or a face selector is needed; exporting the assembly to `.iam`/`.stp` and the drawing.
- Risk: the largest (language, add-in, server, Inventor testing). Do it only after direction B runs reliably.

## Suggested order

1. **A** (small, independent, needs no Inventor): do it first, so people can see and download components.
2. **B** (medium): needs a machine with Inventor; it paves the way for C because it produces separate Parts.
3. **C** (large): do it last, building on B.

For each direction: write a spec in `docs/superpowers/specs/`, a plan in `docs/superpowers/plans/`, test on the server first (`Cadflow.Api.Tests`), and test the add-in part on a machine with Inventor.
