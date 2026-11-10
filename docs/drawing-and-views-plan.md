# Plan: image gallery, 2D drawing (PDF) and section dimensions

## Context
Today a job returns only 1 iso image + .ipt + .stp (`InventorAddin/InventorAddin/Web/InventorJobExecutor.cs`, the `Run` method, `TryCapturePicture`). Needed: (1) let the user see several images after the build, (2) export a 2D drawing, (3) write the dimensions (dim) on the sections automatically. Agreed with the user: PDF, a standard layout (front/top/side + iso + a main section), automatic dims per product, a multi-image gallery.

**Last step when executing:** copy this plan into `docs/drawing-and-views-plan.md` (the user asked for it in `docs/`; we were in plan mode so it could not be written yet), and add 1 reference line in the README.

## Design

### 1. Images from several angles (the cheapest, do it first)
- `InventorJobExecutor`: split out `TryCapturePicture(path, orientation)`; capture the iso, front, top, right angles → `ID_iso.png`, `ID_front.png`, `ID_top.png`, `ID_right.png`. Reuse the Fit + GetExtents/SetExtents logic (15% margin).
- Upload with `kind = "image"`; put the angle name in the file name so the web can group them. No change to the server API (`/artifacts` already accepts any kind).
- `fe/app.js` `renderJob`: replace the single `<img>` with a gallery: a large image + a strip of thumbnails, click to switch/zoom (a simple lightbox, still building the DOM with `h()`/textContent). Add CSS in `fe/styles.css`.

### 2. 2D drawing → PDF
- A new file `Services/DrawingBuilder.cs` (the same style as `TransformerBuilder`): takes the part saved as `.ipt`, creates a `DrawingDocument` with the template from `FileManager.GetTemplateFile(kDrawingDocumentObject, metric)`.
- The base view (front), then `AddProjectedView` for top/right and 1 iso; choose a scale that fits the sheet from the overall size (`RangeBox`), rounded to a standard scale (1:5, 1:10, 1:20…).
- Export PDF through the PDF TranslatorAddIn (GUID `{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}`), the same way `TryExportStep` does it. Do not save the .idw by default (the user chose only PDF); `kind = "pdf"`.
- Call it in `Run` after saving the .ipt, while the part is still open; close the drawing in a `finally` like `CloseQuietly`. A drawing error only reports progress and carries on (like STEP), it does not fail the job.
- Web: a "Drawing" card with an `<iframe>`/`<embed>` to view the PDF + a download link. The server `ContentTypeFor` already has `.pdf`.

### 3. Sections and automatic dimensioning
- Sections: `DrawingViews.AddSectionView` on the front view (a vertical cut through the center) and the top view (a horizontal cut) for both products; for the transformer add a cut through the bushing/conservator axis if needed.
- Dims: each product has a fixed "dim set", declared next to the parameter definitions (the same file `BoxParams.cs`/`TransformerParams.cs` or a separate `DimensionPlan` class) so the server and the add-in do not drift:
    - Box: length/width/height, wall thickness, lid clearance.
    - Transformer: tank length/width/height, base/cover thickness, radiator spacing, bushing diameter + length, conservator diameter + height.
- How to place them: use the `DrawingCurve` of the view + `sheet.CreateGeometryIntent(curve, point)`, then `sheet.DrawingDimensions.GeneralDimensions.AddLinear/AddDiameter`; choose the edge by its extent coordinates (against the `RangeBox` of the model) instead of by order, so it does not depend on the number of bushings/fins.
- **A spike is needed first** (the API is uncertain): the exact signature of `AddSectionView` (it needs a cutting-line sketch), whether `DrawingCurve` exposes the right edge in the section view, and the fallback `RetrieveDimensions`/dims from the sketch of the model. Try it with DevRunner (outside the repo, `...\Temp\devrun`) on a real Inventor before writing the main code.
- Avoid overlapping dims: place dims with an increasing offset outside the view frame.

### 4. Server / contract
- No new endpoint is needed. We can add `kind = "pdf"` to the convention and an optional `summary` (number of views, number of dims).
- Optional (a later phase): an "Include drawing" checkbox on the form (`fe/app.js`) → add a field to the request; on by default.

## Files to edit/create
- create `InventorAddin/InventorAddin/Services/DrawingBuilder.cs`
- edit `InventorAddin/InventorAddin/Web/InventorJobExecutor.cs`
- edit `fe/app.js`, `fe/styles.css`
- edit `README.md`; create `docs/drawing-and-views-plan.md`

## Verification
1. DevRunner spike: build a box + a transformer, create the drawing, export the PDF, open it to look at it with a screenshot/Read.
2. Build the add-in (`dotnet build` of the solution), **restart Inventor** (the DLL is locked while it runs).
3. Place a box and a transformer order from the web, confirm: succeeded, the image gallery has 4 images that are not cropped, the PDF can be viewed on the job page, the dims have the right values compared with the parameters, the section shows correctly.
4. Edge cases: 1 and 8 bushings, 6 radiator fins, the smallest/largest tank (the drawing scale still fits the sheet, dims do not overlap).
5. A drawing error does not fail the job (check by forcing an error).

## Risks
- Automatic dim placement is the hardest part; it may need several rounds of adjustment on a real Inventor.
- Each add-in trial needs an Inventor restart (~4 minutes); use DevRunner to iterate quickly.

---

## Results (updated after the spike on Inventor 2027)

### Drawing conventions applied (general mechanical standards, ISO/TCVN)
| Item | Convention | How it is done |
|---|---|---|
| Projection method | ISO 128: **first-angle projection** (the plan view **below** the front view, the side view on the **right**) | `AddProjectedView` places the views for first angle; template `ISO.idw` |
| Sheet size | ISO 5457 (A4..A0, landscape) | `DrawingBuilder.ChooseSheet`: the smallest sheet that fits at a scale not smaller than 1:20, otherwise a larger sheet, up to A0 |
| Scale | ISO 5455: 5:1, 2:1, 1:1, 1:2, 1:5, 1:10, 1:20, 1:50, 1:100, 1:200, 1:500, 1:1000 | Pick the largest scale that still fits the sheet; the iso view uses half the scale |
| Line types, arrows, text height, dim style | ISO 128/129 | Handled by the template `ISO.idw`, the code does not override them |
| Dim placement | ISO 129: dims outside the view, the first row ≥ 10 mm from the view, each following row ≥ 8 mm from the previous; no redundant dimensions | Offsets of 15 mm and 26 mm; each overall dimension is written only once |
| Unit | mm, no unit symbol | A metric template; the dim shows `2240,00` |
| Layout | Leave a 20 mm binding margin on the left, do not cover the title block (bottom right corner) | `LeftRoom`, `TopRoom`; the iso view in the empty space above the title block |
| Section | A-A cutting line through the center, a chain line, arrow symbols, hatching | `AddSectionView` on the front view |

When the company has its own standard (for example a GE Vernova template), replace the template in `DrawingBuilder.Build` (`GetTemplateFile`) and the layout constants at the top of the file; leave the rest as it is.

### Findings from the spike (Inventor API, easy to get wrong)
- `GetTemplateFile(kDrawingDocumentObject, metric)` does **not** give a metric template: it returns `Standard.idw` (inches, Letter size). You must pass `DraftingStandardEnum.kISO_DraftingStandard` (the file `Metric\ISO.idw`).
- The ISO template is first-angle projection: a view placed above the front view becomes the **bottom** view, not the plan view.
- The sketch of the view (the cutting line) uses coordinates in **model size (not yet multiplied by the scale)**, with the origin at the view center; the sketch must be added to `view.Sketches`, not `sheet.Sketches`. If you give coordinates in cm on the paper, the cutting line is only 1/scale long and the section shows only a thin slice.
- A section is always aligned with its parent view: the `Position` parameter only works along the projection direction.
- `DrawingCurve.Segments[1]` is a `DrawingCurveSegment` (with `StartPoint/EndPoint`), it cannot be cast to `LineSegment2d`.
- A horizontal edge of a round object (bushing, conservator) appears as a circle seen at an angle (RangeBox almost 0 high), not as a straight segment. Pick the outermost edge with `Evaluator2D.RangeBox` over every kind of curve, then place the `GeometryIntent` with a point that lies on the curve.
- `DrawingView.DrawingCurves` may include the sketch of the cutting line (outside the view frame): it must be filtered.
- A model built along the Z axis (the box) needs the view direction `kBottomViewOrientation` to get a correct front view; a model built along the Y axis (the transformer) uses `kFrontViewOrientation`.
- Creating a drawing changes the active view: capture the images of the angles **before**, build the drawing **after**.

### Implementation
- `Services/DrawingBuilder.cs`: builds the drawing + exports the PDF through the TranslatorAddIn (`{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}`).
- `Web/InventorJobExecutor.cs`: captures 4 angles (iso, front, top, right) then calls `DrawingBuilder`; a drawing error is only a warning, the job still succeeds. The summary gains `drawingViews`, `drawingDims`, `drawingScaleDenominator`.
- `fe/app.js`: the image gallery (a large image + thumbnails) and the PDF viewer frame. The server returns the file inline so the browser displays it.
- The dim chain on the section: transformer (base, tank body, cover); box (bottom, height) — the box levels have not been verified on a real Inventor.

### Not done yet / not verified yet (updated 2026-11-06)
- [x] Run through the real web flow (2026-11-06, Inventor 2027.2): the transformer order and the box order both succeeded, with `.ipt`, `.stp`, 4 images, `.pdf`. Transformer: A3 1:20, 5 views, 7 dims. Box: A4 1:2, 5 views, 6 dims; the Z-up view direction is correct.
- [ ] The multi-angle image gallery and the PDF viewer frame on the web (`fe/app.js`, `fe/styles.css`) were just written, not yet run in a browser.
- [ ] **Small bug, the box drawing**: the dim chain on the section has overlapping text ("2,0038,00") because the bottom is only 2 mm thick; the text needs spreading out or the too-small dim dropped. The label of the box cutting line (`A / .`) lacks the zone code compared with the transformer drawing, the cause is not clear.
- [ ] The dims on the section only have the base/tank/cover height chain. There are no dims yet for bushing diameter + length, conservator diameter, radiator spacing (plan section 3).
- [ ] The section only cuts vertically through the center of the front view; there is no horizontal section (on the plan view) or a section through the bushing/conservator axis yet.
- [ ] The transformer is a solid block, so the section is fully hatched; there is no hollow shell/internal detail yet.
- [ ] GE Vernova's internal standard is not applied (no documents); the general ISO is used. When their `.idw` template is available: change `GetTemplateFile` and the layout constants in `DrawingBuilder`.
- [ ] The iso view is at a fixed position (0.72 W, 0.3 H); avoiding overlap with the dims/section at every size has not been checked. The smallest/largest cases were only checked to "produce a PDF", each drawing was not studied closely.
- [ ] There is no "Include drawing" option on the form yet; the original `.idw` is not saved (PDF only); DWG has been exported since 2026-11-08 (translator `{C24E3AC2-122E-11D5-8E91-0010B541CD80}`, see `r2-dashboard-gea-roundtrip-plan.md`, section 4).
- [ ] The error path (Inventor hanging/a dialog) and several workers have not been tested.
- [ ] The README is not updated for the transformer product, the drawing and DevRunner (the trial tool lives outside the repo, `%TEMP%\devrun`).

- [ ] The web UI (the image gallery, the PDF frame) has not been self-tested in a browser; only the correctness of the downloaded files has been confirmed.
