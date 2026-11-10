# inventor-generator-box

Web ordering → job queue → an Inventor add-in builds the model → returns the files. The plan is in `docs/` (step **W0 + part of W2** of section 15.7 in `cadflow-order-to-model-plan.md`).

```
fe/  (static web)  ──►  be/Cadflow.Api (ASP.NET Core, job queue)  ◄──  Inventor add-ins (take jobs, build the model, upload the results)
```

| Folder | Role |
|---------|---------|
| `fe/` | The configurator (a form generated from the schema, rules checked through the API), the order list, a job page that tracks progress in real time. React + Radix UI Themes (Vite); `npm run build` is needed to create `fe/dist`. |
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

## Configuration

- **Add-in**: `%AppData%\InventorBoxAddin\worker.json` (created the first time): `enabled`, `serverUrl`, `workerKey`, `workerName`, `pollSeconds`. The worker name on the server is `<workerName>-<add-in>` (for example `PC01-generator`). Logs are in `%AppData%\InventorBoxAddin\logs`.
- **R2 (optional)**: `dotnet user-secrets set "R2:AccountId" ... --project be/Cadflow.Api` for `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey` (or the environment variables `R2__AccountId`...; add `R2:Endpoint` if you use the EU jurisdiction). When all are set, the worker uploads results straight to the (private) R2 bucket and the web downloads through presigned URLs; otherwise results are kept in `data/artifacts` as before. The startup log says which mode is in use.
- **File retention**: `Retention:Days` (default 0 = keep forever) deletes the result files of finished jobs older than that many days; the job information stays.
- **Server**: `be/Cadflow.Api/appsettings.json`: `Urls`, `Worker:Key`, `ConnectionStrings:Cadflow`. **Change the default `Worker:Key` `dev-worker-key` before exposing the server beyond localhost.**
- To run Inventor on another machine: set `Urls` to `http://0.0.0.0:18080` and point the add-in `serverUrl` at the server (preferably behind a VPN/private network, see section 10 of the plan).

## Worker API (called by the add-in, header `X-Worker-Key`)

`POST /api/worker/heartbeat` · `POST /api/worker/claim` · `POST /api/worker/jobs/{id}/progress|artifacts|complete|fail?worker=...`

A job with no heartbeat for 90 seconds is returned to the queue by itself; at most 3 attempts.
