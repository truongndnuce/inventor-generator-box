# Smart Drawing – Demo Runbook (English narration)

Layout: **Inventor = left half, Web App (Chrome) = right half.** Record the full screen with Bandicam (1920x1080, 30 fps). Run `powershell -File docs\demo\preflight.ps1 -Layout` first.
Do not show `be/Cadflow.Api/.env` or any terminal. Web App is `http://localhost:18080`, Smart Vault `http://localhost:18080/plm/`.

## Before recording (warm-ups)
- Build the substation once (Scene 3) and open its model in Smart Vault so the Autodesk translation (~1 min) is cached.
- Confirm Dashboard has historical jobs and Vault has models and PDFs.
- Send one throwaway chat message to confirm the AI key works.

## Scenes

### 1. Intro (~20 s) — `#/`
Show split screen; point at the green "worker online" badge.
> "This is Smart Drawing. On the right, the web app where orders are placed. On the left, Autodesk Inventor, which builds the models automatically."

### 2. Standard orders (~4 min) — `#/` New order
Repeat for **Power transformer, Gas turbine, Water-cooled generator, Circuit breaker, Disconnector** (not Box):
1. Pick **Product** and **Configuration**.
2. Change one or two parameters; show a rule error turning a field red, then fix it.
3. Fill **Requested by**, click **Submit order**.
4. Glance at Inventor while it builds. Optionally try **Quick preview** once.
5. Finish with `#/orders` showing the queue.
> "Every product is parameter-driven. Rules are validated live, and each order is picked up by Inventor with no manual modeling."

### 3. Electric substation (~2 min) — New order → **Import .gea**
1. Choose `docs/examples/substation-demo.gea`.
2. Adjust "Phase spacing (Z)", "Disconnector to circuit breaker (X)", "Circuit breaker to transformer (X)".
3. **Submit order**; show Inventor assembling the disconnector, three breaker poles and the transformer.
> "Existing products can be combined into one electric substation: power transformer, circuit breaker and disconnector in a single assembly."

### 4. Chat order (~1.5 min) — `#/chat`
Use a chip or type: "Default transformer, tank 2 m long". Show the job card, click **Open job**.
> "Or just describe what you need in plain language, and the assistant creates the order."

### 5. Dashboard and job detail (~1.5 min) — `#/dashboard`, `#/jobs/<id>`
Tiles, Jobs per day, Average build time, filters. Open a finished job: inputs, Result gallery, downloads (Inventor part, STEP, PDF, DWG, BOM), 2D drawing, Timeline.
> "The dashboard tracks every job. Each job keeps its inputs, images, drawing and output files."

### 6. Smart Vault (~1.5 min) — **Open in Smart Vault** (`/plm/`)
Show folder `<product>-<id>` with `image`, `drawing`, `model`, `bom`; show State dropdown and version history.
> "When a job finishes, results are uploaded automatically to Smart Vault, where they are stored, versioned and go through a lifecycle."

### 7. Forge Viewer (~1.5 min) — `/plm/#/item/<id>`
Open the substation **`.stp`** in the `model` folder (the `.iam` shows "Model is empty" because its part references are separate files): rotate, zoom, click a part to see "Selected part". Then open a PDF drawing.
> "Models and drawings open right in the browser with the Autodesk viewer. No CAD license needed."

### 8. Components (~1 min) — `#/components`
Open `tank-base-cover`: parameters, `INCLUDE lib:tank-base-cover`, **Order this component**, **Download starter script (.gea)**.
> "Components are reusable building blocks that any script can include."

### 9. Config (~1 min) — `#/config`
Choose a product, show the configuration matrix, open **+ New configuration** (do not save), open the ⋮ menu: Edit, Duplicate, Set as default, **Order with this**.
> "Configurations are templates per product, with default values, so ordering is one click."

### 10. Outro (~10 s)
> "Smart Drawing: from order to model, drawing and vault, automatically."

## Fallbacks
- Chat fails (503): set `Chat__AutoCreate=false` or skip the scene and cut in post.
- Build is slow: queue the next order and show pre-built jobs for Scenes 5–7.
- Viewer stuck on "Preparing the 3D view…": use the pre-warmed substation model.
- Avoid **Cancel** (confirm dialog blocks browser automation).
