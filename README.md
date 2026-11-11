# inventor-generator-box

Web ordering → job queue → an Inventor add-in builds the model → returns the files. The plan is in `docs/` (step **W0 + part of W2** of section 15.7 in `cadflow-order-to-model-plan.md`).

```
fe/  (static web)  ──►  be/Cadflow.Api (ASP.NET Core, job queue)  ◄──  Inventor add-ins (take jobs, build the model, upload the results)
```

| Folder | Role |
|---------|---------|
| `fe/` | The configurator (a form generated from the schema, rules checked through the API), the order list, a job page that tracks progress in real time. React + Radix UI Themes (Vite); `npm run build` is needed to create `fe/dist`. |
| `fe-plm/` | The **PLM vault** front-end (Docs-style: folder tree, files table, versions, states). A separate Vite app on the same API; the server serves its build at `/plm/`. See *PLM vault* below. |
| `be/Cadflow.Api/` | The API: products, orders, jobs, file download, the worker API. It shares the `*Params.cs` files (Models) with the add-in, so the rules exist in one copy only. It also serves `fe/`. |
| `InventorAddin/` | The Inventor 2027 add-in suite, **one add-in per product** but sharing one **Cadflow** tab on the Part ribbon (see the *Add-in suite* section below). `InventorAddin.Core/` is the shared library; its `Web/` folder is the **web worker**: it asks the server for jobs, builds on the Inventor UI thread, exports `.ipt`, `.stp`, `.png`, `.pdf`. |

## Run

1. **Server + web** (needs the .NET 10 SDK, Node.js 20+ and **PostgreSQL 15+**; the web is React + Radix UI Themes, built with Vite):
   ```
      createdb cadflow                           # first time only; the tables are created when the server starts (see docs/database.md)
      cd fe && npm install && npm run build      # creates fe/dist, which the server serves
   cd .. && dotnet run --project be/Cadflow.Api
   ```
      To edit the UI: `cd fe && npm run dev` (http://localhost:5173, forwards `/api` to the server on :18080).
      Open http://localhost:18080. Jobs live in PostgreSQL (`ConnectionStrings:Cadflow`); result files, scripts and product configurations live in `be/Cadflow.Api/data/` (change it with the `DataDir` setting). Tests: `dotnet test be/Cadflow.Api.Tests` (needs PostgreSQL, see `docs/database.md`).
2. **Add-in**: build `InventorAddin/InventorAddin.sln` (each add-in writes its own `.addin` file into `%AppData%\Autodesk\Inventor 2027\Addins`), then open Inventor 2027. When loaded, each add-in connects to the server on its own and takes only the jobs of its own product. The add-in window title shows `web: connected`.
3. Place an order on the web: the job goes `queued → running → succeeded`, and the job page shows the images, STEP and `.ipt`.

## PLM vault

A document vault for results that must outlive the job retention: folders, files with a number, **version** and **state** (In Work → Under Review → Approved → Released). Each saved job gets a folder named `<product>-<job id>` with fixed `image`, `drawing` and `model` sub-folders (`.png` → image, `.pdf/.dwg` → drawing, `.ipt/.stp` → model); files are **copied** into the vault, so retention does not touch them. You can also upload any file into any folder; a file with a name that already exists becomes a new version (and goes back to In Work). Users are free-text names (no login yet).

```
cd fe-plm && npm install && npm run build     # creates fe-plm/dist, served by the server at http://localhost:18080/plm/
cd fe-plm && npm run dev                      # http://localhost:5174/plm/ (forwards /api to :18080; set VITE_MAIN_APP_URL=http://localhost:5173/ to link back to the orders app)
```

On a finished job page, **Save to PLM** calls `POST /api/vault/from-job/{jobId}`. The API (`/api/vault`): `GET tree`, `GET folders[/{id}]`, `POST folders`, `PATCH|DELETE folders/{id}`, `POST folders/{id}/items?name=&by=` (the body is the file), `GET items/{id}`, `POST items/{id}/versions`, `GET items/{id}/versions/{n}/file?download=`, `PATCH items/{id}/state`, `DELETE items/{id}`, `GET search?q=&kind=&state=`. Files go to R2 under `vault/` when R2 is configured, else to `data/vault/`. Settings: `FrontendPlm:Path` (default `fe-plm/dist`).

## Add-in suite

Each add-in is one DLL, one `.addin` file and its own GUID, so it can be turned on/off independently in Inventor (Add-In Manager), but all of them add a panel to the same **Cadflow** tab (`CF_Tab`) on the Part ribbon; whichever add-in loads first creates the tab.

| Add-in (`InventorAddin/Addins/`) | Panel | Window | Web jobs it takes (`productCode`) |
|---|---|---|---|
| `Box` | Box Generator | A box with a lid: presets, preview, Apply | `box` |
| `Transformer` | Transformer | `.gea` script (power transformer) | `transformer`, `imported` (a script imported from the web) |
| `GasTurbine` | Gas Turbine | `.gea` script (gas turbine) | `gas-turbine` |
| `Generator` | Water-cooled Generator | `.gea` script (water-cooled generator) | `generator` |

- Each add-in has its own web worker; when it takes a job it sends the list of `products` to `POST /api/worker/claim`, and the server hands out only jobs of those codes (no `products` = accepts all, like the old worker). Each add-in builds one job at a time.
- The base class `CadflowAddIn` (Core/Host) handles the shared ribbon, the window, the worker and refreshing when the document changes; `ScriptProductAddIn` provides a ready-made Script window for products built with `.gea`.
- **Adding a new product**: (1) `Models/XxxParams.cs` + `Scripts/xxx.gea`; (2) add one line in `Core/Web/ScriptedProducts.cs` and in `be/Cadflow.Api/Products.cs` (+ link the file in `Cadflow.Api.csproj`); (3) add an `Addins/Xxx` folder with a class that inherits `ScriptProductAddIn` (copy `Addins/Generator`, change the GUID/name) and add it to the solution.
- **Reusable components**: `INCLUDE lib:<name>` inside `BEGIN_FORM` and `BEGIN_BUILD` inserts the parameter group and the build steps of `InventorAddin.Core/Scripts/lib/<name>.gea` (currently `tank-base-cover`, the base/tank/cover of the power transformer, which `transformer.gea` uses). To add a library, put a `.gea` file in `Scripts/lib/`. The server stores and sends the worker the expanded copy. The **Components** tab on the web lists the libraries and lets you download a file, download a starter script or order the component itself. Details in `sa-docs/gea-language-spec.md` section 20.
- Water-cooled generator (`generator`): a box-shaped stator body with vertical ribs on both sides, a water cooler box hanging below the body with inlet/outlet pipes, a terminal box + two fans on top, two bearing covers + pedestals, a rotor shaft, an excitation cabinet, a base frame (solid blocks, no cut-away view of the rotor/core as in the picture). The rules in `GeneratorParams.cs` (shared with the server) and the `FORM` block of `generator.gea` are the same.
## `.gea` scripts (SmartAssembly style)

The language spec is in `sa-docs/gea-language-spec.md`. The interpreter is in `InventorAddin/InventorAddin.Core/Gea/` (lexer, parser, semantic check, FORM, BUILD; no Inventor dependency), the Inventor Host and builders are in `Services/Gea/`. Sample scripts: `InventorAddin/InventorAddin.Core/Scripts/` (`generator.gea` (generator), `turbine.gea` (gas turbine), `tank-level1.gea`, `transformer.gea`, `transformer-hv.gea`: a detailed outdoor power transformer with fin-type radiators, a conservator with supports, an oil pipe, two rows of HV/LV bushings, a drain valve and a warning plate).

Used in the Transformer / Gas Turbine / Generator add-ins (the window is the **Script (.gea)** panel, showing only the templates of the product; the default template opens by itself the first time):
1. **Open…** a `.gea` file, or choose a **Template** and press **Open template** (opens an editable copy in `%AppData%\InventorBoxAddin\scripts`).
2. The **Form** tab is generated from the `BEGIN_FORM` block: fields show/hide with `IF`, and `MIN`/`MAX`/`ASSERT` errors turn the right field red.
3. Edit the script right in the window (errors have a line and column; double-click to jump to the line) or use **Edit externally**: when you save the file the add-in reloads it on its own; turn on *Apply automatically when the script file is saved* so the model updates by itself.
4. **Apply**: saves the script, then in **one Transaction** deletes every body/feature/sketch/work plane the script created last time, synchronizes the parameters as User Parameters, and runs `BEGIN_BUILD` again. An error at any step leaves the model unchanged; Ctrl+Z undoes the whole Apply. Geometry the user drew themselves (without the `GEA_Owner` mark) is kept.
5. The script and values are stored in the Part (AttributeSet `GEA_Script`): reopening the `.ipt` makes the add-in reload the script so you can keep editing (**Load from model**).

Builders available for `CREATE_FEATURE builder:<Name>`: `Tank`, `Cover`, `Block`, `Cylinder`, `Bushing`, `Radiator` (manifest in `Gea/GeaManifests.cs`). The web worker builds the transformer with `Scripts/transformer.gea` and the box with `Scripts/box.gea`, so the returned `.ipt` can also be opened and edited with the Script tab. On the web, every job built from a script has an **Export .gea** button (the script + the values set) and **Import .gea** on the New order page rebuilds the form from that file (see `docs/r2-dashboard-gea-roundtrip-plan.md` and `sa-docs/gea-language-spec.md` section 19).

## Product Designer (make a `.gea` without writing it)

The **Designer** page of the web (`#/designer`) lets someone who does not know the `.gea` syntax make the script of a new part: name and prefix, parameters in groups (type, unit, default, range, "show only when"), variables and rules (`ASSERT`, error or warning), and the build text. The page generates the `.gea` file, checks it with the real compiler while you type (each message appears next to the parameter, rule or build line that caused it), and **Try it** runs a dry run on the simulation host: the form for any values, the broken rules, and the list of builder calls. Download the file, or press **Order from this design** to import it. The design is stored at the end of the file as comments (`!@DESIGNER`, `sa-docs/gea-language-spec.md` section 21), so **Open .gea file…** loads it again; a file written by hand opens as text only. A draft is kept in the browser.

API (`be/Cadflow.Api/Designer/`; problems with the design are data in a 200 answer, 400 is a body that is not valid JSON, 413 a body over 512 KB):
- `GET /api/gea/builders` lists the builders with their inputs and outputs.
- `POST /api/gea/designer/generate` body `{ model }` returns `{ gea, issues, diagnostics, lineMap }`; `gea` is null while there are errors.
- `POST /api/gea/designer/parse` body `{ text }` returns `{ hasModel, model, bodyChanged, reason }`.
- `POST /api/gea/designer/dryrun` body `{ model, values, texts }` returns `{ valid, stage, form, errors, warnings, trace, log }`; it stops after 5 seconds.

This first version makes one `PART` and the build is written as text; a visual parts tab, a build preview and publishing a design as a named product come later (`docs/superpowers/specs/2026-11-11-gea-designer-design.md`, and `...gea-general-builders-design.md` for the general builders).

## Configuration

- **Add-in**: `%AppData%\InventorBoxAddin\worker.json` (created the first time): `enabled`, `serverUrl`, `workerKey`, `workerName`, `pollSeconds`, `queues`. The worker name on the server is `<workerName>-<add-in>` (for example `PC01-generator`). Logs are in `%AppData%\InventorBoxAddin\logs`.
- **R2 (optional)**: `dotnet user-secrets set "R2:AccountId" ... --project be/Cadflow.Api` for `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey` (or the environment variables `R2__AccountId`...; add `R2:Endpoint` if you use the EU jurisdiction). When all are set, the worker uploads results straight to the (private) R2 bucket and the web downloads through presigned URLs; otherwise results are kept in `data/artifacts` as before. The startup log says which mode is in use.
- **File retention**: `Retention:Days` (default 0 = keep forever) deletes the result files of finished jobs older than that many days; the job information stays.
- **Server**: `be/Cadflow.Api/appsettings.json`: `Urls`, `Worker:Key`, `ConnectionStrings:Cadflow`. **Change the default `Worker:Key` `dev-worker-key` before exposing the server beyond localhost.**
- To run Inventor on another machine: set `Urls` to `http://0.0.0.0:18080` and point the add-in `serverUrl` at the server (preferably behind a VPN/private network, see section 10 of the plan).

## Queues, job kinds and priority

Like SDS (Main Build / Main Filter, priority 0 to 100 with 0 the most urgent), jobs wait in one of two queues and the server decides the queue and priority from the **kind** of the job (a web user does not pick a priority, there is no login yet):

| Kind | Queue | Priority | What the worker builds |
|---|---|---|---|
| `generate` (default) | `build` | 20 | Full build: `.ipt .stp .png .pdf .dwg` |
| `preview` | `filter` | 0 | A fast job: one isometric picture only, no `.ipt`, STEP or drawing. The web has a **Quick preview** button on New order |
| `export` | `build` | 20 | A rebuild of an older job with the same script (by SHA-256) and values, for example to get back files deleted by the retention policy (`POST /api/jobs/{id}/rebuild`, the **Rebuild files** button on the job page) |

- Inside a queue the lowest priority number goes first, then the oldest job. A retried job keeps its kind, queue and priority.
- Each machine says which queues it takes and in which order: `queues` in `worker.json` (default `["build","filter"]`: a full build first, a preview only when no build waits; `["filter","build"]` does the opposite; `[]` takes any queue by priority). This is the same idea as the node settings of SDS.
- Change the queue or priority of a kind in `appsettings.json`: `"Queue": { "Kinds": { "preview": { "Queue": "filter", "Priority": 0 } } }`.
- `GET /api/queues` shows the depth of each queue, the oldest wait and the online workers that take from it; `GET /api/jobs/{id}` of a queued job has `queuePosition`; `GET /api/jobs` and `GET /api/stats` accept `kind=generate,export`. The Dashboard keeps previews out of its default list and its build-time statistics.
- A worker that does not know about queues (an old add-in) takes any queue by priority and builds a preview as a full job; an old server ignores `queues`.

## Worker API (called by the add-in, header `X-Worker-Key`)

`POST /api/worker/heartbeat` · `POST /api/worker/claim` (body `worker`, `products`, `queues`; the job it returns has `kind`) · `POST /api/worker/jobs/{id}/progress|artifacts|complete|fail?worker=...`

A job with no heartbeat for 90 seconds is returned to the queue by itself; at most 3 attempts.
