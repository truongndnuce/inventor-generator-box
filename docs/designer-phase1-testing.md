# Product Designer, phase 1: how to test it again

Written 2026-11-11 so the work can be checked on another machine without the chat history.
Design: `docs/superpowers/specs/2026-11-11-gea-designer-design.md` (section "Phase 1 as built"). Plan: `docs/superpowers/plans/2026-11-11-gea-designer-phase1.md`. Language spec: `sa-docs/gea-language-spec.md` section 21.

## What phase 1 is

A web page (**Designer**, `#/designer`) that makes a `.gea` file for a new part without writing the syntax: product name and prefix, parameters in groups, variables, rules, and the build as text. The server generates the file, checks it with the real compiler, and can dry-run it on the simulation host. The design is stored at the end of the file as comments (`!@DESIGNER` block) so the Designer can open the file again.

Commits on branch `truong/feat/turbin-gas-2` (oldest first):

| Commit | Content |
|---|---|
| `509868a` | Design model and validator |
| `eb379ee` | `DesignGenerator` (model to `.gea`) |
| `969602b` | Compiler diagnostics mapped to model paths |
| `815147f` | `!@DESIGNER` block, `GeaExport.StripBlock` keeps it |
| `1631397` | `DesignRunner` (dry run) |
| `f63134a` | Builder catalog DTO |
| `e64ec2f` | Endpoints in `Program.cs` + `DesignerApi.cs` |
| `5033076` | Designer page |

The docs (README, language spec section 21, the two specs and the plan) may be in a later commit; check `git log` and `git status`.

## 1. Automated tests (any OS with the .NET 10 SDK and PostgreSQL)

```
dotnet test be/Cadflow.Api.Tests
```

- Needs PostgreSQL (some tests create a temporary database). Default server `Host=localhost;Username=admin`; change it with the environment variable `CADFLOW_TEST_PG`.
- Expected on 2026-11-11: **344 passed, 0 failed.** The Designer tests are in `DesignValidatorTests`, `DesignGeneratorTests`, `DesignDiagnosticsTests`, `DesignBlockTests`, `DesignRunnerTests`, `BuilderCatalogTests`, `DesignerApiTests`; fixtures in `be/Cadflow.Api.Tests/Fixtures/designer/`.
- To run only the Designer tests: `dotnet test be/Cadflow.Api.Tests --filter "FullyQualifiedName~Design|FullyQualifiedName~BuilderCatalog"`.
- On macOS there is no `timeout` command; run `dotnet test` directly.

## 2. Backend by hand (curl)

Start the server (the old process on :18080 must be stopped first, or use another port):

```
cd fe && npm install && npm run build       # fe/dist is served by the server
cd .. && dotnet run --project be/Cadflow.Api                       # http://localhost:18080
# or, next to a running server:  dotnet run --project be/Cadflow.Api --urls http://localhost:18081   (set DataDir=<temp folder> so it does not share data)
```

Save a design as `gen.json`: take `be/Cadflow.Api.Tests/Fixtures/designer/box-form.design.json`, wrap it as `{ "model": <the file> }` and add one step to `steps`: `{ "kind": "raw", "text": "<the BEGIN_BUILD..END_BUILD content of InventorAddin/InventorAddin.Core/Scripts/box.gea, without those two lines>" }`.

| Check | Command | Expected |
|---|---|---|
| Builders | `curl -s localhost:18080/api/gea/builders` | 6 builders: Tank, Cover, Block, Cylinder, Bushing, Radiator |
| Generate | `curl -s -X POST localhost:18080/api/gea/designer/generate -H 'content-type: application/json' --data @gen.json` | `issues: []`, `diagnostics: []`, `gea` is a text of about 102 lines ending with the `!@DESIGNER` block |
| Import accepts it | `POST /api/gea/import` with `{ "text": <gea> }` | 200, `form.ok: true`, product name "Box with lid" |
| Parse | `POST /api/gea/designer/parse` with `{ "text": <gea> }` | `hasModel: true`, `bodyChanged: false`, the product is "Box with lid" |
| Parse after a hand edit | change one character of the script above the block, parse again | `hasModel: true`, `bodyChanged: true` |
| Dry run, defaults | `POST /api/gea/designer/dryrun` with `{ "model": ... }` | `valid: true`, `stage: "build"`, `trace` has `CREATE_FEATURE builder:Tank Length=100 mm ...` and `builder:Cover ...` |
| Dry run, broken rule | same with `"values": {"BX_Height":20,"BX_Bottom":10,"BX_Wall":10}` | `valid: false`, `stage: "form"`, an error at `rules[1]`: "Wall thickness must be less than (height - bottom thickness)." |
| Bad JSON | `-d '{bad'` to `generate` | 400 with `{ error }` |
| Too large | a body over 512 KB, or `parse` with a text over 256 KB | 413 |

## 3. The page by hand (needs the server running with the new code)

Open `http://localhost:18080/#/designer` (or `npm run dev` in `fe/` and `http://localhost:5173/#/designer`; the dev server forwards `/api` to :18080).

Checked on 2026-11-11 (browser, server on :18081):
- [x] Empty design shows the Product tab with messages for the empty name and prefix (shown once each).
- [x] A saved draft is offered on the next visit ("Continue the last draft / Start new").
- [x] Try it with a Box design: `valid`, "The simulated build ran.", the form, "Builder calls (2 steps)".
- [x] Height 20, Bottom 10, Wall 10: "not valid", the rule message appears.
- [x] Parameters tab shows the groups and rows; Text tab shows the generated file with "Show the model block".

**Not checked yet; please do these:**
1. **Build the Box from scratch on the page.** Product: name "Box with lid", prefix `BX_`. Add the two groups and nine parameters (see `box-form.design.json`), the two variables on the Rules tab, the seven rules, and on the Build (text) tab press **Insert the Box build as an example**. Expected: "no problems" next to the title; Try it is `valid`.
2. **Messages next to the element.** Rename a parameter to something without the prefix (`Length`): the row shows the error. Write a rule that uses a name that does not exist: the rule row shows the compiler message. Put a wrong line in the Build text: the message shows `line N` with the line you typed.
3. **Parameter types.** Add one of each type (LENGTH, ANGLE, INTEGER, DOUBLE, STRING, CHECKBOX, RADIO). Changing the type must reset what does not apply (no min/max for STRING, options for RADIO as a comma list, default TRUE/FALSE for CHECKBOX). Set "Show only when…" on a parameter to a CHECKBOX name and see the field appear and disappear on Try it.
4. **Download .gea** (Text tab): the file `<product-name>.gea` is saved.
5. **Open .gea file…** with that file: the same design comes back (Product tab and Parameters tab equal). Then edit one character above the `!@` block in a text editor and open it again: the page asks to open the model (hand edits dropped) or as text. Open a hand-written script such as `InventorAddin/InventorAddin.Core/Scripts/box.gea`: it shows as text with "This file was not made with the Designer".
6. **Order from this design** (Try it tab): goes to the Script order page with the same form; the order can be submitted, the job waits for a worker (building it needs the Windows machine).
7. **Reload** the page: the draft offer appears; **Start new** gives an empty design.
8. Dark and light theme look right; the page works at a narrow width.

## 4. What this phase does not test or do

- **No geometry.** The dry run lists the builder calls with their values; it does not build a part. Building a real part from a Designer file needs the Windows machine with Inventor 2027 and a worker (the file is an ordinary `.gea`, so the existing worker path applies).
- **Raw build text only.** The Parts tab, `DRAWING_LEVEL`, Build preview, AI first draft, publishing a design as a named product, and the general builders (Frame, Profile, Extrude, ...) are later phases (design sections 11 and 13, and `docs/superpowers/specs/2026-11-11-gea-general-builders-design.md`, whose phase 0 is a feasibility spike on Windows).
- Known limitation: a diagnostic from inside an `INCLUDE lib:` library has the line number of the library file, so it is shown without a location.
- A dry run that runs past 5 seconds returns "Dry run timed out"; the thread behind it stops by itself at the interpreter's limit of 1,000,000 statements.

## 5. If something fails

- Server returns 404 for `/api/gea/designer/...`: the running process is an old build; stop it and start the new one.
- Page shows an old UI: run `npm run build` in `fe/` (the server serves `fe/dist`), or use the dev server.
- Tests cannot reach PostgreSQL: start it or set `CADFLOW_TEST_PG`.
- Golden file differs (`DesignGeneratorTests`, `Fixtures/designer/all-types.gea`): the generator output changed on purpose or by accident; read the diff before updating the golden file.
