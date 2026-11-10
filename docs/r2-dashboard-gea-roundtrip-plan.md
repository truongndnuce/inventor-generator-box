> **Update:** the job metadata moved from `jobs.json` to PostgreSQL, see `database.md`. The passages below that mention `jobs.json` are the context at the time the plan was written.

# Plan: the Cloudflare R2 document store, DWG download, 3 web pages and two-way `.gea`

This document supplements `cadflow-order-to-model-plan.md` (sections 3.1, 7, 8, 15), `drawing-and-views-plan.md` (the drawing export part) and `smartassembly-to-inventor-mapping.md` (section 11, script safety).

## 1. Goals and out of scope

**Current state**
- Artifacts live on the server disk: `be\Cadflow.Api\data\artifacts\<jobId>\<name>` (`JobStore.ArtifactDir`).
- The web has only 2 pages: New order (`#/`) and Orders (`#/orders`), plus the job page `#/jobs/<id>`.
- The drawing is PDF only, no DWG yet. `DrawingBuilder` closes the drawing without saving an `.idw`.
- The endpoint `GET /api/jobs/{id}/artifacts/{name}` does not check the job status, so a file can be downloaded while the job is still `running`.
- There is no `.gea` import or export on the web. The worker only runs the `transformer.gea` hard-coded in the add-in folder.

**Goals**
1. Use Cloudflare R2 as the store for sections, drawings and the other result files.
2. Let users download the drawing as `.dwg` (still viewed as PDF).
3. Split the web into 3 pages: **New order**, **Ordered** (jobs that are running), **Dashboard** (the status of jobs that have run).
4. A running job must not open its results. This rule is enforced in both the UI and the server.
5. Two-way `.gea`: importing gives the UI the form and values; exporting from the web is also a `.gea`.

**Out of scope**: real login and permissions, moving `jobs.json` to a database, a policy to delete old artifacts.

## 2. Decisions (settled)

| # | Topic | Decision |
|---|---|---|
| R1 | Import `.gea` | Script + values (settled): the server compiles the FORM block to generate a dynamic form; the order runs that script itself, `WorkItem` carries the script content |
| R2 | Upload to R2 | Presigned URL (settled): the server issues the PUT, the worker uploads straight; download with a presigned GET, issued only when the job is `succeeded` |
| R3 | User | A name typed by hand (settled): uses `RequestedBy`, kept in the browser, no login yet |
| R4 | Dashboard | Statistic cards, build time, filtering and reviewing old jobs, duplicating an order (settled) |

## 3. R2 as the document store

### 3.1 How it is stored

| Item | Content |
|---|---|
| Bucket | One private bucket, public access off |
| Key | `jobs/<jobId>/<name>` (the same `safeName` rule as now) |
| Metadata | `Artifact` gains `Key`, `Sha256`, `ContentType`. `Name`, `Kind`, `Size` stay as they are |
| Job metadata | `jobs.json` stays as it is in this phase |
| Configuration | `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey` via user-secrets or environment variables, not written into `appsettings.json` |
| Library | AWSSDK.S3, endpoint `https://<AccountId>.r2.cloudflarestorage.com`, region `auto` |
| Dev | When R2 is not configured, fall back to the local disk as now |

### 3.2 Upload flow (worker)

```
Worker --POST /api/worker/jobs/{id}/artifacts/presign {name,kind,size,sha256}--> Server
Server --> {putUrl, key, expiresAt}
Worker --PUT putUrl (file)--> R2
Worker --POST /api/worker/jobs/{id}/artifacts/commit {name,key}--> Server
The server: HEAD on R2 to check the size, write the ArtifactInfo
```

- Replaces the current raw-body upload (`Program.cs`, lines 114-130, and `ServerClient.UploadAsync`).
- The R2 key lives only on the server. The worker only receives a short-lived URL.
- Only a `running` job can ask for a presign. The server checks that the worker holds that job.

### 3.3 Download flow

- `GET /api/jobs/{id}/artifacts/{name}` (`Program.cs`, lines 76-84) changes as follows:
    - a job that is not `succeeded` returns `409 Conflict`;
    - a `succeeded` job returns `302` to a presigned GET (about 5 minutes).
- The download button uses `Content-Disposition: attachment`. Viewing images and PDF uses inline.
- CORS on the bucket is needed for the origin of the UI, if `<embed>` of the PDF or `<img>` loads straight from R2.

## 4. DWG drawing

- `DrawingBuilder` (`Services\DrawingBuilder.cs`, around line 119, next to `ExportPdf` at lines 274-284) gets `ExportDwg` through the DWG translator of Inventor.
- The DWG translator id is not in the code yet (only PDF and STEP are). It must be looked up in a spike, the same way it was done for PDF.
- `InventorJobExecutor` uploads an extra file with `kind = "dwg"`.
- `ContentTypeFor` (`Program.cs`, line 144) gets `.dwg` → `image/vnd.dwg` and `.gea` → `text/plain; charset=utf-8`.
- UI: the drawing is viewed as PDF (`<embed>`), a **Download DWG** button sits next to PDF, STEP and images.
- Consider saving the original `.idw` (an item still open in `drawing-and-views-plan.md`).

## 5. Ba trang web

The router is currently in `fe\app.js` (lines 269-282), the navigation bar in `fe\index.html` (lines 12-15).

| Page | Route | Content |
|---|---|---|
| New order | `#/`, `#/new/<code>` | Like `renderConfigurator` today, plus an **Import .gea** button (section 6.2) |
| Ordered | `#/orders` | Lists only `queued` and `running` jobs. Clicking opens the progress page: a timeline, the current step, a Cancel button. **No** gallery, PDF or download buttons. A finished job leaves the list by itself and moves to the Dashboard |
| Dashboard | `#/dashboard` | See below |

### 5.1 Dashboard

- **Statistic cards**: the number of jobs per status (queued, running, succeeded, failed, cancelled) and the success rate.
- **Build time**: the average and a chart per day, computed from `StartedAt` and `FinishedAt`.
- **Job table** filtered by product, status, date range, requester (`RequestedBy`). By default it shows the jobs of the name saved in the browser, with an "all" switch.
- **Review old jobs**: clicking a `succeeded` job opens the result page with the gallery, PDF, DWG download, STEP, PDF, `.gea`.
- **Duplicate an order**: an "Order again" button pours the inputs of the old job into New order (reusing the `sessionStorage` mechanism of "Edit and resubmit", `fe\app.js` line 232).

### 5.2 The job detail page `#/jobs/<id>`

- Keep the route for old links. `queued` and `running` show only progress. The Result part shows only when `succeeded` (the gate already exists at `fe\app.js` lines 246 and 250).
- The UI is not enough. **The server must block** (section 3.3), so it cannot be bypassed by typing the artifact URL.

### 5.3 New API

| Endpoint | Purpose |
|---|---|
| `GET /api/jobs?status=&product=&requestedBy=&from=&to=&page=` | A filtered and paged list. Ordered uses `status=queued,running` |
| `GET /api/stats?from=&to=` | Counts per status, the average build time, the number of jobs per day |

`store.List()` already returns every job newest first, so in the first phase filtering and grouping happen in memory.

## 6. Two-way `.gea`

### 6.1 Export (web → `.gea`)

- `GET /api/jobs/{id}/export.gea` returns a `.gea` file made of the script that ran and the input values of the job.
- The job also stores `ScriptText` and `ScriptSha256` to export exactly the copy that ran, even if the original script changes later.
- How the values are written into the file follows `sa-docs\gea-language-spec.md` and is similar to the `GEA_Script` mechanism already stored in the `.ipt` (`Services\Gea\GeaDocumentService.cs`, lines 53 and 113).
- The **Export .gea** button is on the result page and the Dashboard.

### 6.2 Import (`.gea` → web)

1. The user chooses a `.gea` file in New order (or drags and drops).
2. The UI calls `POST /api/gea/import`. The server compiles with `GeaScript.Compile` and `EvaluateForm` (`InventorAddin\Gea\GeaInterpreter.cs`, lines 79 and 89).
3. The server returns the form schema (the same shape as `/api/products/{code}`: `groups`, `parameters`, `defaults`) and a list of diagnostics with line and column.
4. The UI builds the form from the schema and fills in the values from the file. Errors show right away, and submitting is blocked while errors remain.
5. Submit sends the `script` in `POST /api/jobs`. `WorkItem` carries `ScriptText`.
6. `InventorJobExecutor` (currently hard-coding `TransformerScript = "transformer.gea"`, line 35) runs the received script instead of reading a file by product name.

The interpreter does not depend on Inventor, so link the `Gea\` folder into `Cadflow.Api` the way `BoxParams.cs` is linked (csproj lines 15-16). The server then checks the script itself, without Inventor.

### 6.3 Safety

`smartassembly-to-inventor-mapping.md` section 11 says the person ordering only supplies values. Decision R1 relaxes this, so the following safeguards are needed:

- A limit on the file size and the number of executed steps.
- `GeaChecker` runs before a job is accepted and blocks statements outside the allowed list.
- No running external programs, no writing outside the job folder (the ban in section 11 stays).
- A job created from an imported script is marked `ScriptSource = "imported"`, shown clearly in the UI.
- Remaining risk: there is no login or role yet, so anyone who can reach the web can import. Permissions are needed if it is opened beyond the internal network.

## 7. Phases

| Phase | Work | Done when |
|---|---|---|
| P0 | Block artifacts by status on the server; split into 3 pages; `GET /api/jobs` with filters | A `running` job opening an artifact URL gets 409; Ordered has only running jobs; results can only be seen from the Dashboard |
| P1 | R2 and presign (section 3) | The worker uploads straight to R2; a `succeeded` job downloads through a presigned GET; no new file stays on disk |
| P2 | DWG (section 4) | A new job has a `.dwg` that opens in AutoCAD or Inventor, with a download button |
| P3 | `/api/stats` and the Dashboard (section 5.1) | Cards, charts, filters and Order again work |
| P4 | Export `.gea` (section 6.1) | The exported file imports back, giving the same values |
| P5 | Import `.gea` and run the uploaded script (sections 6.2, 6.3) | Importing `tank-level1.gea` gives the right form, the order finishes, the result matches a run from the add-in |

- [x] P0 (2026-11-08): the server blocks artifacts while the job is not `succeeded` (409), `GET /api/jobs` has filters, the web has 3 pages. Checked with a simulated worker: the artifact of a `running` job → 409, a finished job → 200, filtering by `status`/`product`/`requestedBy` is correct. The new UI has only passed a syntax check, it has not been seen in a browser.
- [x] P1 (2026-11-08): `R2Store.cs` (AWSSDK.S3 4.0.104.3, presigned PUT 15 minutes / GET 5 minutes), the worker does `POST .../artifacts/presign`, then PUTs straight to R2, then `.../artifacts/commit` (the server HEADs to check the object, then records `ArtifactInfo.Key`); a download is a 302 to a presigned GET, only when the job is `succeeded`, `?download=true` forces `attachment`. If R2 is not configured or the server is old, upload through the disk stays as before (old jobs without `Key` are still read from disk). Tested for real with the bucket `sa-inventor`: a box job put all 8 files on R2 (ipt, stp, 4 png, pdf, dwg), no file on disk, the downloaded PDF is valid, a URL without a signature is rejected by R2 (private bucket). Configuration: `dotnet user-secrets set "R2:AccountId|Bucket|AccessKeyId|SecretAccessKey" ... --project be/Cadflow.Api` or the environment variables `R2__AccountId`... Retention (2026-11-08): `RetentionService` deletes the result files (R2 and disk) of jobs finished more than `Retention:Days` days ago (default 0 = keep forever), sweeping once an hour; the job record, parameters and script are kept so the Dashboard and "Order again" do not break, the job records `artifactsPurgedAt` and the job page says the files were deleted. Tested: a simulated job 3 days old with `Retention:Days=1` had its files removed and an event recorded (the object disappearing from R2 was not checked separately, only that the delete command reported no error). Not done: bucket CORS (only needed if the browser `fetch`es straight from R2; `<img>`/PDF/download do not need it now; an Object Read & Write token usually also lacks the right to set CORS, it must be set in the Cloudflare dashboard).
- [x] P2 (2026-11-08): `DrawingBuilder.ExportDwg` uses the translator `{C24E3AC2-122E-11D5-8E91-0010B541CD80}` ("Translator: DWG", Inventor 2027.2), no options needed. Run for real through web → worker: the box (A4 1:2) and transformer (A3 1:20) jobs both have a `.dwg` (header `AC1032` = AutoCAD 2018), downloadable with `Content-Type: image/vnd.dwg`. A DWG error is only a warning, it does not spoil the PDF. The DWG file has not been opened in AutoCAD. The "Download DWG" button on the web has only passed a syntax check.
- [x] P3 (2026-11-08): `GET /api/stats?days=&product=&requestedBy=` (counts per status, success rate, average build time, per day in server time). The Dashboard has 5 cards, 2 bar charts (jobs/day, build time/day, with tooltips), a 7/14/30/90 day choice, filters for product/status/"Only mine", and an "Order again" button in the job table and on the result page. Screenshots were taken with headless Edge with 2 real jobs; clicking "Order again" and filtering by hand in a browser have not been tried.
- [x] P4 (2026-11-08): `GET /api/jobs/{id}/export.gea` returns the script used + a `!@` value block at the end (the format is in `sa-docs\gea-language-spec.md` section 19, the code in `be\Cadflow.Api\GeaExport.cs`). The server keeps the script by SHA-256 in `data\scripts\`; `Job` has `ScriptName`, `ScriptSha256`. The web has an "Export .gea" button on the job page and the Dashboard. Checked: the exported file compiles clean with `GeaScript.Compile`, reads back all 20 values, exporting again does not double the block, a real transformer job still builds. The `box` product at first had no script so it could not be exported; this was solved with `Scripts\box.gea` (see the "box.gea" step below). The exported file has not been opened in the Inventor add-in.
- [x] P5 (2026-11-08): import `.gea` and order from a script. API: `POST /api/gea/import` (compile, store by SHA-256, return the form + the values read from the `!@` block), `POST /api/gea/{id}/form` (recompute the FORM whenever a value changes: hidden/shown fields, `ASSERT`), `POST /api/jobs` takes `scriptId` + `inputs` + `textInputs` (the job has `productCode = "imported"`, `scriptSource = "imported"`). The server links `InventorAddin\Gea\*.cs` so it compiles scripts itself; `WorkItem` carries `ScriptText`, and the worker runs exactly that script. Supports number, checkbox, radio and text (`!@TEXT`) fields. Web: an "Import .gea" button in New order, the page `#/import/<scriptId>` builds a dynamic form, "Order again"/"Edit and resubmit" reopen the right script. Run for real on Inventor 2027.2: importing the transformer export file (built, with PDF/DWG), importing `tank-level1.gea` (has the radio `TR_Paint`) → built → exported → imported again gives the same `scriptId` and values; the built-in transformer still builds normally through `ScriptText`. The server rejects a script with a syntax error, over 256 KB, empty, values that violate `MIN/MAX/ASSERT`, a wrong radio choice, an unknown `scriptId`; nested `FOR` loops are stopped by the limit of 1,000,000 statements (~1.2 seconds). Limits: the drawing of an imported job has no height dimension chain on the section (only the elevation levels of the built-in box/transformer are known), the vertical direction is assumed Y-up like the transformer; there is no login so anyone who can reach the web can import.

**Extra step: `box.gea` (2026-11-08).** `InventorAddin\Scripts\box.gea` describes the box with the `Tank` + `Cover` builders (the same `BX_*` parameters, defaults, MIN/MAX and 7 `ASSERT` rules as `BoxParams.Validate`). `Products.cs` attaches `ScriptFile: "box.gea"`, the server links this file like `transformer.gea`; the worker builds the box from the `ScriptText` the server sends down, and an old server that sends no script falls back to the C# gateway (`InventorBoxGateway`). Checked: (1) comparing the FORM rules of the script with `BoxParams.Validate` on 120,000 random and boundary value sets (about 36,600 valid sets): not one differs; (2) a real build on Inventor 2027.2: the defaults give 68.559 cm³, 2 bodies, 0.0686 kg, A4 1:2, 5 views, 6 dimensions, identical to the old C# version; a large set (220×140×90, wall 3, corner radius 8...) gives 621.493 cm³; both match the analytic formula to 3 decimal places; (3) exporting a box job then importing it again, ordering from the imported file gives the same 621.493 cm³, and values that violate the rules are rejected. Differences to know about: the box model from the script has a vertical axis of **Y** (the builders work Y-up like the transformer), the old C# version used Z; the body and lid are named `BX_Box`/`BX_Lid`; the `BX_*` parameters in the `.ipt` are no longer User Parameters that drive the shape (the builder creates the shape straight from the values), so editing a parameter in Inventor no longer changes the shape as in the C# version. The interactive box panel in the add-in still uses the parametric C# version.

## 8. Risks

| Risk | How to reduce it |
|---|---|
| An R2 key leak | The key lives only on the server, via user-secrets or environment variables; the worker only has a short-lived URL; the bucket is private |
| A presigned PUT expires on a large file | 15 minutes for the PUT; ask for a presign again on a 403 error |
| The DWG translator differs by Inventor version | Look up the id in a spike; record the DWG version tried; a DWG error does not fail the whole job (a warning in the timeline) |
| A malicious or too heavy script | Section 6.3: the checker, limits on size and step count, an isolated job folder, a timeout |
| An old job has artifacts on disk and no `Key` | Serve a disk fallback if `Key` is empty; migrate to R2 gradually if needed |
| `jobs.json` rewrites the whole list on every change, slow with many jobs | Accepted in this phase; moving to a DB is out of scope, noted for later |
| A hand-typed user name can be forged | Accepted (R3); used only for filtering, not for permissions |

## 9. Next things to do right away

1. Do P0: check the status in `GET /api/jobs/{id}/artifacts/{name}`, add filters to `GET /api/jobs`, build the route `#/dashboard` and change `#/orders` to show only running jobs.
2. Create the R2 bucket and an API token (Object Read & Write limited to the bucket), configure CORS.
3. Spike the DWG translator on Inventor 2027 with the transformer and box drawings, record the translator id in `drawing-and-views-plan.md`.
4. Decide the format for writing values into the `.gea` on export, check it against `sa-docs\gea-language-spec.md`.
