# Plan: an "order → automatic Inventor model build → return the dossier" system

This document continues `inventor-addin-plan.md`. The add-in and the DSL in that plan are the **model-building core**; this document adds the **ordering web, the queue and the worker machines** to turn that core into a service.

---

## 1. What can be seen in the video

The video is about 43 seconds, a screen recording, **with no sound**. The picture is blurry so some small text is a guess.

### 1.1. The web interface "CADFlow – Assembly orders"
- There is a product catalog (the "All products" button). The demo product is a **Power transformer**, the title says "full Smart Assembly demo".
- The **order form** has:
    - Rating (power, for example 1000 kVA) and HV voltage class (high-voltage class, for example 35 kV). A small note says that **dimensions and price come from the catalog**, and that **only the voltage classes valid for the chosen rating are allowed**.
    - Cooling (a choice box with two values, looking like ONAN and ONAF).
  - Options: air neutral bushing, manhole on the cover, shipping configuration (no breather).
    - Paint (paint color, for example Grey, Green).
  - Documents: Drawing and PDF.
    - Requested by, and a **Submit order** button.
- The **job detail page** (an id like `AJ-20261105-090449-933f`):
    - The status moves: **waiting for a machine** ("Waiting for an Inventor worker to pick up the job...") → **building** ("The worker is building the assembly...") → **succeeded**.
    - It shows Product, Created, Started, Finished, **Worker** (the name of the machine running Inventor), the list of **Inputs**, and a **Summary** line (mass in kg and price).
    - When done there is a **Picture** (a screenshot of the model) and a **Downloads** section: PDF drawing, STEP model, BOM, Costing, ZIP package, Report, Picture, Inventor drawing.
- The address looks like `100.x.x.x:18080/assembly/jobs/...` and there is a path `api/assembly/...`, so there is an API behind it. The `100.x` range is usually a private network such as Tailscale (a guess).

### 1.2. The worker machine (left half of the screen)
- This is a Windows machine (the text "Activate Windows" is visible) with Inventor open, viewed remotely through a browser.
- When a job runs, **the model changes live**: the tank changes material and finally turns **green** matching the paint option chosen.
- Then Inventor opens a **drawing** automatically: views, balloons, an A-A section at a scale of 1/50 and a **Parts List table** (Tank, Cover, hex bolt ISO 4017 M16x70, HV bushing, LV bushing, radiator ONAF, conservator...).
- The model has many components: tank, cover, high- and low-voltage bushings, radiator fins, a conservator tank.

### 1.3. What the video shows and does not show
| Shows | Does not show |
|----------|---------------|
| The flow order → queue → worker → returned dossier | Whether AI or an agent is used (no chat box or prompt is seen) |
| A form generated per product, with constraints between choices | How the rules are written inside (DSL, SmartAssembly or code) |
| Building an assembly, drawing, BOM, price, STEP, picture, ZIP, report | How errors, retries, several workers at once are handled |

Conclusion: this is a **configure-to-order** system that runs deterministically. If there is AI, it only needs to sit at the front to turn a verbal request into the values of the form (see section 9).

---

## 2. Goals

1. The user picks a product, fills in the form, clicks submit.
2. The system builds the Inventor model exactly by the rules, with no one sitting and operating it.
3. It returns the **dossier**: picture, STEP, PDF and IDW drawings, BOM, price, report, ZIP package.
4. The job status can be seen, the order history, and the dossier downloaded again.
5. Adding a new product is mostly through the **product definition** (DSL, catalog, drawing template), without changing the core.

### Out of initial scope
- Payment, quotes with approval, complex permissions.
- Running several Inventors in parallel on one machine.
- A 3D preview right on the web.

---

## 3. Overall architecture

```
User ──► Web (form, job list, file download)
                 │
                 ▼
                        API Server ──► Database (products, orders, jobs, workers)
                                  │    └──► File store (result dossiers)
                 ▼
                        Job queue
                                  ▲   (the worker asks actively, only an outbound connection is needed)
                 │
                Worker (a Windows machine with Inventor 2027)
                        ├─ A loop that takes jobs and reports status
                        ├─ DSL core: reads the product definition, checks rules, creates the build plan
                        ├─ Inventor Adapter: builds the assembly, drawings
                        └─ Exporter: STEP, PDF, IDW, picture, BOM, price, report, ZIP
```

### 3.1. The components
| Component | Role | Suggestion |
|------------|---------|-------|
| **Web** | A form generated from the product definition, the job page, file download | Build the form from the schema of the product, not by hand for each form |
| **API Server** | Catalog, receives orders, re-validates values with the DSL core, manages jobs, serves files | ASP.NET Core, to share the DSL library with the worker |
| **Database** | Products and versions, orders, jobs, artifacts, workers | PostgreSQL |
| **Queue** | Holds waiting jobs | Start with a job table in the database, workers take with a row lock (`FOR UPDATE SKIP LOCKED`); no SQS or broker needed yet |
| **File store** | Stores the dossier | A folder on the server at first, later Cloudflare R2 (see `r2-dashboard-gea-roundtrip-plan.md`, section 3) |
| **Worker** | Runs Inventor and builds the dossier | A .NET application running on a Windows machine with Inventor |
| **Component library** | Parts, iParts, sample assemblies (bushings, radiator fins, conservator...) | Version-managed, kept in a fixed folder or a shared store |

### 3.2. Principles
- **The worker asks the server, the server does not call the worker.** The machine running Inventor only needs an outbound connection, opens no inbound port, and is easy to put behind a firewall or in a private network.
- **One single product definition** used in three places: the web form, the server-side rule check, the model build on the worker. No rule is written twice.
- **The DSL core does not depend on Inventor** (exactly as in the DSL plan). The server and the worker share this library.
- **The Inventor Adapter does not depend on the add-in host**: it takes an `Inventor.Application` object and works. This way the same Adapter runs both in the add-in (dockable window) and in the worker.
- **Each job is immutable and repeatable**: it stores the input values, the product version, and the component library version.

---

## 4. Product definition

A product is a **package** of several parts, all of them data:

| Part | Content | Example (power transformer) |
|------|----------|---------------------|
| Parameters and options | Name, type, unit, default, a list of choices | Rating, voltage class, cooling, paint, the choice boxes |
| Relations between choices | Which choice is valid with which | A rating allows only some voltage classes |
| Catalog tables | Dimensions, part codes, price by rating, material | Tank dimensions by rating, bushing code, price |
| Rules | Conditions and calculations | The number of radiator fins by cooling, the height of the conservator |
| Build plan | Which part is placed where, which parameter is assigned | Place the tank, cover, bushings, conservator; turn the manhole on or off |
| Drawing template | The IDW template, the list of views, tables, scale | An A3 page, an A-A section, a Parts List |
| Price formula and report | How to compute the price, the report template | Price = parts + machining + accessories |

Suggested organization:
- The rules and build plan part is the **DSL** in section 5c of `inventor-addin-plan.md`.
- **The catalog is kept separate from the rules**, stored as tables (CSV or JSON) so people who do not write code can update it.
- Each product has a **version**. An order that was placed stays tied to the version at the time it was placed.

---

## 5. The lifecycle of a job

| Status | Meaning | Shown on the web |
|------------|---------|-------------------|
| Queued | The order is received, waiting for a worker | Waiting for an Inventor machine to pick it up |
| Running | A worker is working, with small steps | Building the assembly (with the step: check, build, drawing, export, upload) |
| Succeeded | Done, with a dossier | Picture, summary (mass, price), download buttons |
| Failed | There was an error | A readable error message and a resubmit button |
| Cancelled | The user cancelled | |

### 5.1. The worker steps
1. **Take the job** (lock the job for this worker, record the start time).
2. **Re-check the input** with the DSL core. If wrong, stop and report the error before Inventor is even opened.
3. **Prepare**: open or check Inventor, create a working folder just for the job, copy the component library needed.
4. **Build the assembly** by the build plan.
5. **Compute indicators**: mass from Inventor, price from the catalog and formula.
6. **Create the drawing** from the template, place views, balloons, tables.
7. **Export**: picture, STEP, PDF, IDW, BOM, price table, report, ZIP package.
8. **Upload** to the server, write the summary, set the status Succeeded.
9. **Clean up**: close documents, delete the temporary folder, return Inventor to a clean state.

### 5.2. Durability
- **Heartbeat**: the worker reports periodically. A job past its deadline with no heartbeat is returned to Queued or marked as failed.
- A **maximum time** for each step and for the whole job.
- **Retry** with a limited count. An error caused by wrong input is not retried.
- **One job at a time per Inventor.** To go faster add worker machines, do not run in parallel inside one Inventor.
- **Cache**: the same product, the same version and the same input reuse the old dossier (hash the input to match).
- **Inventor supervision**: if it hangs or a dialog blocks it, kill the process and restart it, and the job goes to a retry.

---

## 6. The Inventor part on the worker

### 6.1. How to run Inventor
| Approach | Pros | Cons |
|------|-----|-------|
| **The worker is a separate process, driving Inventor through COM** | Easy to debug, separate from Inventor, reuses the Adapter | Needs Inventor to run in a logged-in desktop session |
| **An add-in running inside Inventor, taking jobs itself** | Closest to the API | Calls must be moved back to the Inventor UI thread, hard to debug |
| **APS Design Automation** (Inventor running in the cloud, no machine of our own) | No machines to manage, easy to scale | The plugin must be packaged to the service's requirements; **check whether Inventor 2027 is supported and which .NET target**; limits on libraries and drawings |

Recommendation: start with a **separate worker process driving Inventor through COM** on one Windows machine. Your company already uses APS, so the third option is worth keeping for later, once the Inventor 2027 support is clear.

### 6.2. Notes on operating Inventor
- Inventor needs a **logged-in desktop session** (it does not run reliably as a background Windows service). Configure auto-login and have the worker start automatically when the machine boots.
- Run in a **no-dialog mode** (`SilentOperation`) and handle unexpected dialogs (a missing file, a license, an update).
- License: each worker machine needs an Inventor license.
- Use a separate working folder for each job, with fixed paths, to avoid conflicts when building a multi-file assembly.

### 6.3. Building the assembly
- The **component library** is parametric parts and iParts (tank, cover, bushings, radiator fins, conservator...). Each has Parameters named by a convention such as `BX_` in the add-in.
- The build plan says: which part to insert, where to place it (a constraint or a precomputed position), which parameters to assign, which parts to turn on or **suppress** by option (manhole, neutral bushing...).
- **Paint** is assigning an Appearance to the part, exactly as in the video (the tank changes to green).
- Prefer to **place by precomputed coordinates** or by simple, fixed constraints. Complex constraints are the biggest source of errors in automation.

### 6.4. Automatic drawings
- Use an **IDW template** that already has the title block, the Parts List table, the dimension styles.
- Views are placed by the layout declared in the product definition (scale, position, section).
- Automatic balloons and dimensions can land in the wrong place or overlap. This is the **hardest and most fragile part**, so do it last and accept placing balloons by simple rules.
- Export the PDF from the IDW with the Inventor translator.

### 6.5. The files produced
| File | Source |
|-------|-------|
| Picture (PNG) | Captured from the Inventor view at a fixed angle |
| STEP | The Inventor translator |
| PDF and IDW drawings | From the automatic drawing |
| BOM | Read from the assembly BOM or from the build plan, exported as CSV or XLSX |
| Costing | The price formula from the catalog and the mass |
| Report | A summary of parameters, mass, price, the file list (PDF or HTML) |
| ZIP | Packages the files above |

---

## 7. Data model (conceptual level)

| Table | Main content |
|------|----------------|
| Product | Code, name, description |
| ProductVersion | Product, version number, the definition package (DSL, catalog, drawing template), publication status |
| Order/Job | Job id, product and version, **input as JSON**, requester, status, current step, created/started/finished times, worker, summary (mass, price), error, input hash |
| Artifact | Job, kind (PDF, STEP, BOM...), path, size, hash |
| Worker | Machine name, status, last heartbeat, Inventor version and library version |
| JobEvent | The log of steps and messages of each job |

The job id has a date-time form with a random segment (like `AJ-20261105-090449-933f` in the video) to be easy to read and not collide.

---

## 8. API (resource level, not detailed yet)

| Group | What it must do |
|------|--------------|
| Catalog | List products, get the form schema of one product |
| Orders | Submit an order (the server re-checks with the DSL core), list jobs, view a job's details |
| Dossier | Download each file, download the ZIP package |
| Worker | Take a job, report progress and heartbeat, report completion or an error, upload files |

Status updates on the web: start with periodic **polling**; upgrade to SSE or SignalR when needed.

---

## 9. An optional AI layer (not required)

If you want an "agent":
- The AI does just one thing: **read a verbal request or the customer's documents and fill in the form** (or propose values for the user to approve).
- The values must pass the **rule check of the DSL core** like any other order. The AI never builds the model itself or skips the rules.
- This keeps the result deterministic and testable, and the AI is just another way to enter an order.
- Do it last, once the pipeline is stable.

---

## 10. Security and operations

- Run in the **internal network or a VPN**. The video uses plain HTTP on a private network; when expanding, HTTPS and login are advisable.
- The worker authenticates to the server with its own key for each machine.
- The dossier may contain price and design information: permission viewing by user or group.
- Limit the size and kind of uploaded files.
- A full log for each job (to help find errors).
- Back up the database and the file store; the component library has version control.

---

## 11. Implementation phases

### G0. Choose a pilot product and a mock service skeleton
- Choose the **hollow box with a cover** (it already has a parameter table) as the pilot product, not starting with the transformer.
- Build the web, API, database, queue, job page, file store.
- **A fake worker**: takes a job, waits a few seconds, returns a sample file. Tests the whole flow and statuses without Inventor.
- **Done when**: placing an order on the web, seeing the job go through Queued → Running → Succeeded and being able to download the file.

### G1. A real worker builds the box
- The worker opens Inventor, uses the DSL core and the Adapter (from `inventor-addin-plan.md`) to build the box.
- Export the picture and STEP.
- Handle errors, the maximum time, restarting Inventor when it hangs.
- **Done when**: a box order on the web produces a picture and STEP matching the parameters.

### G2. Drawing, BOM, PDF
- The IDW template for the box, views, tables, PDF and IDW export.
- BOM and report.
- **Done when**: a complete dossier for the box.

### G3. Catalog, price, rules between choices
- Catalog tables, price formula, mass from Inventor.
- The web form allows only valid choices (like rating and voltage class in the video).
- **Done when**: changing a choice changes the price and the list of valid options with it.

### G4. A multi-component product (the power transformer)
- The target product is the power transformer described in section 5d of `inventor-addin-plan.md` (referencing the GE Vernova image). Levels 1-2 of the transformer are exactly the pilot box of G0-G2, so nothing is redone.
- A versioned component library, multi-part assemblies, on/off options, paint.
- Add a second product close to the transformer (for example an enclosure with radiator fins) to verify the DSL and the Adapter.
- **Done when**: adding a new product is mostly by definition.

### G5. Operations and scaling
- Several workers, cache, notifications, permissions, HTTPS.
- Consider APS Design Automation.
- An AI layer for entering orders if needed.

---

## 12. Relation to the add-in plan

| Part in `inventor-addin-plan.md` | Role in this system |
|--------------------------------------|----------------------------|
| DSL (section 5c) | The language that defines products, used on the web, server and worker |
| Inventor Adapter | Used as it is on the worker, separated from the add-in host |
| Add-in and dockable window | A trial and hand-editing tool on an engineer's machine; still usable to build and preview each definition |
| Preview canvas, presets | Can be reused for the web form (a dimension preview, sample order presets) |
| Phase 2 (the hard-coded box build) | Still kept as the **reference** to compare against the DSL result |

Suggested order: do **G0** (no Inventor needed) in parallel with **Phases 0-2** of the add-in and the DSL branch, then join them at **G1**.

---

## 13. Risks and how to reduce them

| Risk | How to reduce it |
|--------|-----------|
| Inventor hangs, a dialog blocks it, licensing | A no-dialog mode, supervision and restart, a maximum time, limited retries |
| An automatic drawing in the wrong place, overlapping balloons | Do it last, a fixed layout, check with a set of sample images, accept the simple level first |
| Complex assembly constraints break easily | Precomputed positions or simple constraints, test at boundary values |
| Scope growing too early (the transformer from the start) | Pilot with the box, add features only when a real product needs them |
| A change in the component library breaks old orders | Version the library, write the version into the job |
| Rules scattered across the web, server and worker | One single definition, sharing the DSL library |
| One worker machine is a bottleneck | Design the worker to pull jobs from the start so adding machines later is easy |
| APS Design Automation does not yet support Inventor 2027 | Check before investing; keep the separate worker as the main option |
| A leak of price and design | A private network, HTTPS, permissions by user |

---

## 14. Next things to do right away

1. Settle the **pilot product** (recommended: the hollow box with a cover) and the kinds of dossier to return in the first version (picture, STEP, PDF drawing, BOM, price).
2. Settle where the system (the web server) goes and the **Windows machine used as the worker** (with an Inventor 2027 license, already logged in).
3. Do **G0**: the web, API, queue and a fake worker. No dependency on Inventor.
4. In parallel, continue D0 of the DSL and Phases 0-2 of the add-in.
5. Settle the open questions:
      - Who are the users (engineers inside the company or outside customers)?
      - How many kinds of dossier are needed in the first version?
      - Is there a ready library of parts (part, assembly) to build a real product?

---

## 15. A SmartAssembly-style web interface

### 15.1. How the requirement is understood
You want a flow like SmartAssembly (the user enters requirements, **the rules run automatically**, CAD runs behind, the result comes back) but with **all user interaction on the web**, not in the Inventor window. From public information, Sigmaxim also has a web configuration tool and the concept of a "virtual master model"; I do not know the internal details so I only take the general idea: **the configuration interface is separate from CAD, the rules run on the server, CAD is the executor behind**.

Consequences for the earlier decisions:
- **The main UI moves from the WPF dockable window to the web.** The preview canvas becomes the preview on the web. The WPF add-in remains only a development tool (or is dropped). This also removes the risk of hosting WPF in the Inventor dock window in Phase 1.
- **The JSON preset** becomes a preset stored in the database per product.
- The DSL core, the Inventor Adapter and the worker keep their roles.

### 15.2. Three functional areas on the web

| Area | User | Main functions |
|-----|-----------|-----------------|
| **Configurator** (ordering) | The person ordering | Pick a product; a form generated from the definition; check rules as soon as a choice is made (which choices are locked, which values are valid); preview; a rough price; submit the order |
| **Orders and Viewer** (tracking, results) | The person ordering | The job list, live status updates, viewing the real picture and 3D model, the PDF drawing, downloading the dossier |
| **Product Studio** (authoring products) | The engineer writing definitions | Write the DSL, catalog tables, drawing templates; errors by line and column; dry runs without Inventor; publish versions |
| Admin | Administrators | Workers, jobs, the component library, users |

**UI update (see `r2-dashboard-gea-roundtrip-plan.md`, section 5):** the "Orders and Viewer" area was split into 3 pages: **New order**, **Ordered** (only running jobs, results cannot be opened) and **Dashboard** (statistics, filters, reviewing finished jobs, downloading PDF/DWG/STEP/`.gea`, duplicating an order).

### 15.3. The user flow

1. Open the catalog, pick a product (for example the power transformer).
2. The form appears, generated from the **schema of the product**: input fields, choice boxes, checkboxes, units.
3. When a choice changes, the web calls the **DSL core** (on the server, or in the browser if running WebAssembly) to: lock or unlock the dependent choices, report values that violate rules, update the rough price and the preview frame.
4. Click Submit order. The server checks one last time and then creates the job.
5. The job page shows **Queued → Running (with the step) → Succeeded** in real time.
6. When done: a picture from Inventor, a **rotatable 3D model** in the browser, the PDF drawing, BOM, price, report, STEP, IDW, ZIP.
7. The user can **duplicate an order** (take the old input, change a few values, submit again) or save it as a preset.

### 15.4. Preview in three tiers

| Tier | When | Source | Accuracy |
|------|---------|-------|--------------|
| 1. Instant preview | While editing the form | The build plan running in the DSL core, drawn in the browser (a 2D diagram like the canvas already designed, later a rough 3D block with WebGL, for example three.js) | Illustrative, true to proportion but not Inventor |
| 2. A real picture | After the worker finishes building | A screenshot from Inventor | Exact |
| 3. A real 3D model | After it is done | Convert the model to a format viewable on the web | Exact, can be rotated and zoomed |

Ways to get a real 3D model on the web:

| Approach | Pros | Cons |
|------|-----|-------|
| **APS Viewer** (upload the file to APS, translate with the Model Derivative, view with the Viewer) | Your company already uses APS (Model Derivative, Viewer), can see the tree structure and measure | Needs an APS account, translation takes time and money, data leaves the internal network |
| **STEP to glTF converted ourselves** (for example with OpenCascade on the worker or server) then viewed with three.js | Runs fully internally, no cloud dependency | We handle the mesh quality and colors ourselves, and must maintain the converter |
| **Multi-angle images only** (take 4-6 angles from Inventor) | Very simple, enough for a first version | No free rotation |

Recommended roadmap: the first version with multi-angle images only, then add the APS Viewer (since it is familiar), consider an internal glTF if there is a data security requirement.

### 15.5. Product Studio: authoring products on the web
This part is the **most expensive, so do it later**.

- First version: the person writing definitions **edits files in the repo with an ordinary editor** (with a command-line tool to check errors and dry run), no web editor needed yet.
- Later version (web editor):
    - Write the DSL with custom syntax highlighting, line and column errors and parameter name hints (for example using Monaco Editor).
    - **Dry run** without Inventor: check the syntax, run the rules with a sample value set, giving the build plan, price, expected BOM and the preview frame.
    - **A sample value set** stored with the product, rerun automatically on every edit like a regression test.
    - **Draft and publish**: only a published version receives orders; old orders keep the old version.
    - A **dry run on a worker** button creates a trial job (not counted as a real order).
    - Compare two versions.
- Edit the catalog tables on the web (as a table) for people who do not write code.

### 15.6. Suggested technology

| Part | Choice | Note |
|------|----------|---------|
| Frontend | React or Next.js | You already know Next.js; a form generated from a JSON schema |
| Frontend (alternative) | Blazor WebAssembly | Runs the same .NET DSL library right in the browser, with no logic to rewrite; in return the bundle is heavier |
| Running the DSL core | Call an API on the server for the first version | Simple; move to WebAssembly if instant feedback or offline work is needed |
| Editor | Monaco Editor | Cho Product Studio |
| 3D viewing | three.js (preview), APS Viewer (the real model) | See section 15.4 |
| API | ASP.NET Core | Shares the DSL library with the worker |
| Status updates | Polling, then SSE or SignalR | |
| Database, file store | PostgreSQL, a server folder then Cloudflare R2 (see `r2-dashboard-gea-roundtrip-plan.md`) | |
| Login | An internal account or SSO | See the security part in section 10 |

### 15.7. Adjusted roadmap (W0-W5)

| Step | Content | Matching old phase |
|------|----------|------------------------|
| **W0** | Web, API, queue, **a fake worker**; the first Configurator with a **hand-written JSON schema for the box product** (no DSL needed yet); the job page; sample file download | G0 |
| **W1** | The DSL core (D0-D2) replaces the hand-written schema: the form, rules and price are generated from the definition; a command-line tool to check and dry run | D1-D2, part of G3 |
| **W2** | A real worker builds the box, returns multi-angle pictures and STEP | G1 |
| **W3** | Drawing, BOM, PDF, price, report; a real 3D Viewer | G2, G3 |
| **W4** | Instant preview on the web (2D then rough 3D) | The canvas in section 5b, moved to the web |
| **W5** | Product Studio on the web (editor, dry run, version publishing); the transformer product level by level | G4, G5 |

### 15.8. Risks specific to the web direction

| Risk | How to reduce it |
|--------|-----------|
| The preview on the web drifts from the Inventor model | State clearly that it is illustrative, always have the real picture after the build, share the build plan so the two sides do not drift in logic |
| Two geometry generators must be written (Inventor and WebGL) | Keep the build plan abstract (blocks, cylinders, sweeps along a path), each target has its own converter; the first version only needs a 2D diagram |
| A web editor for the DSL costs as much as a project of its own | Do it last; the first version edits files in the repo |
| Design data passes through the cloud (APS) | Consider an internal glTF if the data is sensitive; agree clearly before uploading files to APS |
| The scope is too wide (Configurator, Viewer, Studio, Admin) | Do the Configurator and Orders first; Studio and Admin are a later step |
| Running rules in two places (browser and server) gives different results | The server always re-checks and is the standard; if run in the browser, use the same library (WebAssembly) |

### 15.9. Next things to do right away
1. Settle the scope of "do it on the web" in the first version: only the Configurator and order tracking, or Product Studio too.
2. Choose the frontend (React or Next.js, or Blazor WebAssembly).
3. Do **W0**: the web, API, a fake worker, the box form from a JSON schema.
4. Settle how the real model is viewed in the first version (multi-angle images) and leave the APS Viewer for a later step.
