# Plan: an Inventor add-in that creates objects and adjusts parameters

## 1. Goals

Build an Autodesk Inventor add-in that can:

1. **Create** an object (Part or Assembly) from a predefined template.
2. **Adjust** the parameters of that object after it is created, and the model updates itself.
3. **Recognize again** the object the add-in created, even after the file is saved and reopened.

### Out of scope (first phase)
- No drawings (Drawing) yet.
- No several object kinds at once. Only 1 sample object kind to settle the architecture.
- No installer packaging yet.

---

## 2. Decisions to settle before coding

| # | Question | Choice | Note |
|---|---------|----------|---------|
| 1 | Target Inventor version | **Inventor 2027 (settled)** | Target **.NET 10**, use Visual Studio 2026 and the Inventor 2027 SDK/DeveloperTools.msi. Do not use .NET Framework 4.8 |
| 2 | Object kind | **Part (settled)** | Assembly is kept for Phase 5 if needed |
| 3 | Target shape | **A power transformer referencing the GE Vernova image (direction settled), built in levels** | **Level 1 is exactly the hollow box with a cover** (tank and cover), parameters in section 4. Later levels add bushings, pipes, cooling. Details in section 5d |
| 4 | Parameter list | Name, type, unit, min/max, default | Table in section 4 |
| 5 | Where to create and edit | **On the open model (Part) (settled)** | No new document is created. The add-in works with the active document. See Phase 2 for the handling rules |
| 6 | UI | **The main UI moved to the web (see section 15 of `cadflow-order-to-model-plan.md`).** The WPF dockable window with the preview canvas is now an optional development tool | The preview canvas becomes the preview on the web. Section 5b is kept as a design reference |
| 7 | Saving presets | **A JSON file (settled, temporary storage)** | The sample file `presets.json` comes with the plan. Details in section 5b |
| 8 | How to describe the object | **Build our own language (DSL) for Inventor (direction settled)** | Inspired by the rule-based idea of SmartAssembly, but the syntax and semantics are our own design. Details in section 5c |

---

## 3. Proposed architecture

### 3.1. Layers
- **AddIn Host**: the server layer of the add-in, manages the lifecycle, registers the ribbon, cleans up resources on shutdown.
- **UI Layer (WPF, MVVM)**: the View only binds, the ViewModel holds state and commands, and neither contains Inventor logic; the ViewModel only calls a **service interface** and does not reference Inventor directly. Details in section 16.3 of `cadflow-order-to-model-plan.md`. The backend is ASP.NET Core, the web frontend is ReactJS.
- **Application Layer**: use cases such as `CreateObject`, `UpdateParameters`, `ReadParameters`.
- **Inventor Adapter**: the only layer that calls the Inventor API directly. The other layers only work with their own model.
- **Domain/Model**: defines the object and parameters (name, unit, constraints), independent of Inventor so it is easy to test.
- **Language layer (DSL)**: lexer, parser, semantic analysis, a units-aware expression evaluator, producing an abstract "build plan". Fully independent of Inventor, details in section 5c.

### 3.2. Design principles
- **Parametric first**: every important dimension is bound to a named User Parameter. Edit by changing the Parameter value, not by deleting and redrawing geometry.
- **One place defines parameters**: the name, unit and limits sit in one configuration table, and both the UI and the logic read from it.
- **Wrap every operation in one Transaction** so Ctrl+Z undoes it completely.
- **Run only on the UI thread** of Inventor.

---

## 4. Parameter table (filled in when the shape is settled)

| Parameter name | Meaning | Type | Unit | Default | Min | Max |
|---------------|---------|------|--------|----------|-----|-----|
| BX_Length | Outer length of the box | Length | mm | 100 | 40 | 500 |
| BX_Width | Outer width of the box | Length | mm | 60 | 30 | 300 |
| BX_Height | Height of the box body (lid not included) | Length | mm | 40 | 20 | 200 |
| BX_Wall | Wall thickness | Length | mm | 2 | 1 | 10 |
| BX_Bottom | Bottom thickness | Length | mm | 2 | 1 | 10 |
| BX_CornerRadius | Radius of the vertical (outer) corners | Length | mm | 3 | 0 | 20 |
| BX_LidThickness | Lid thickness | Length | mm | 2 | 1 | 10 |
| BX_LidLip | Height of the lid lip that fits inside the box | Length | mm | 4 | 2 | 15 |
| BX_LidClearance | Gap between the lid lip and the inner wall | Length | mm | 0.3 | 0 | 1 |

#### Shape of the object
- **Box body**: Extrude a rounded rectangle (Length x Width, rounded by `BX_CornerRadius`), then Shell with the top face open at thickness `BX_Wall`, bottom thickness `BX_Bottom`.
- **Lid**: a plate with the same outer size, thickness `BX_LidThickness`, with a lip projecting down by `BX_LidLip` that fits into the box cavity (clear of the inner wall by `BX_LidClearance`).
- **How it is held in the file**: the body and the lid are **two solid bodies in the same Part**, the lid above the box body, separate. If the lid later needs to be exported to its own file, or an assembly with motion is needed, switch to an Assembly (Phase 5).

#### Validity constraints (checked in the UI before applying)
- `BX_Wall` is less than half of `min(BX_Length, BX_Width)` and less than `BX_Height - BX_Bottom`.
- `BX_CornerRadius` does not exceed half of `min(BX_Length, BX_Width)`.
- The inner corner radius (`BX_CornerRadius - BX_Wall`) is taken as 0 if negative, so the sketch does not fail.
- `BX_LidLip` is less than `BX_Height - BX_Bottom` so the lip does not touch the bottom.
- `BX_LidClearance` is less than `BX_Wall` so the lid lip does not stick out past the wall.
- The min/max limits in the table are only a starting point, to be adjusted after testing at the boundary values.

#### Later extensions (not in the first version)
- Stiffening ribs inside the bottom: add `BX_RibCount` (an integer, no unit) to drive a Pattern.
- Screw holes at the four corners, slots or vent holes in the lid.

Notes:
- Name Parameters by one consistent convention (for example with a prefix of the add-in's own) so they do not clash with Parameters the user creates.
- Always assign with an expression that has a unit. The internal unit of the API is cm and radians, and assigning a bare number easily causes errors.

---

## 5. Implementation phases

### Phase 0: Prepare the environment
- [ ] Install Visual Studio 2026 (needed to build a .NET 10 assembly), the Inventor 2027 SDK and DeveloperTools.msi (which installs the Add-in template for Visual Studio).
- [ ] Target framework: .NET 10. See the "What's New" API Help of Inventor 2027.
- [ ] Look at how the `.addin` is declared and how dependency libraries (NuGet) are loaded: Inventor 2027 has a dependency isolation mechanism (`UseInventorAssemblyContext`), so read the documentation before adding packages.
- [ ] Do not use `BinaryFormatter` (it was removed in new .NET). If presets need saving, use JSON.
- [ ] A Regfree add-in (no registry registration), installed through an `.addin` file or an ApplicationPlugins bundle.
- [ ] Create the repo, and the solution structure following the layers in section 3.
- **Done when**: an empty add-in is loaded by Inventor and shows a button on the ribbon.

### Phase 1: Add-in skeleton
- [ ] Register the ribbon tab, panel, button.
- [ ] Open a dockable window that holds an empty WPF UserControl. Do this early because it is the easiest place for the UI to get stuck (how to host WPF in the Inventor dock window, handling the light/dark theme change, the size when stretched).
- [ ] Set up logging to a file (badly needed when debugging an add-in).
- [ ] Debug process: attach the debugger to Inventor, check loading/unloading the add-in.
- **Done when**: clicking the button shows the UI and writes a log.

### Phase 2: Create the sample object
- [ ] Get the active document and check the conditions before creating:
    - No document open: tell the user to open or create a Part.
    - The document is not a Part (for example an Assembly, Drawing): a clear error, do nothing.
    - The Part already has an add-in object (recognized by an AttributeSet): do not create again, switch to the parameter editing mode.
    - The Part already has other geometry or Parameters: warn and let the user confirm before adding.
- [ ] Check for a name clash: if a Parameter with the prefix `BX_` already exists and was not created by the add-in, stop and report, do not overwrite.
- [ ] Give clear names to the sketch, features and the two bodies (for example box body, lid) so they are easy to recognize and clean up.
- [ ] If creation fails halfway, undo everything (Transaction) so no half-built model is left.
- [ ] Create the User Parameters from the parameter table.
- [ ] Create the sketch, dimension constraints bound to the Parameters, then the features.
- [ ] Attach an identifying mark (AttributeSet) to the document to know it is an add-in object.
- **Done when**: clicking the button gives the right model, and the Parameters table of Inventor shows the parameters with the right names and units.

### Phase 3: Adjust parameters
- [ ] Read the parameter list from the open model and show it in the UI, and draw the preview canvas from those values.
- [ ] Edit values in the UI: the canvas changes as you type, min/max and validity constraints are checked, and only clicking Apply pushes into the model and updates it.
- [ ] Handle errors when a value makes the sketch or a feature fail (undo and report the reason clearly).
- [ ] Support Undo through the Transaction.
- **Done when**: changing a parameter in the UI changes the model; entering a wrong value does not break the model.

### Phase 4: Robustness and experience
- [ ] Recognize the object again after the file is saved and reopened.
- [ ] Handle the cases: no open document, a document not created by the add-in, the user editing a Parameter by hand.
- [ ] Clean up COM references, check that Inventor exits cleanly when closed.
- [ ] Presets: load, choose, save, overwrite, delete presets from the JSON file (see section 5b), with handling for a corrupt file or an invalid preset.
- **Done when**: it passes the manual test set in section 6.

### Phase 5 (optional): Extension
- Add a second object kind to verify the architecture, which is also step D4 of the DSL (section 5c).
- Switch to an Assembly if the object has many parts.
- Allow importing parameters from a file (Excel/JSON).
- Package an ApplicationPlugins bundle for a neat install.

---

## 5b. UI and preview canvas design

### Dockable window layout
- **Top: the preview canvas** (see below).
- **Middle: the parameter form**, split into two groups "Box body" (Length, Width, Height, Wall, Bottom, CornerRadius) and "Lid" (LidThickness, LidLip, LidClearance). Each parameter has a label, a number input, the unit mm and a slider over the min/max range. The form is built from the parameter table in section 4, with no hard-coded fields.
- **Bottom: action buttons**: Create (when the Part has no box yet), Apply, Revert to the values in the model, Default. The status bar shows errors or constraint warnings.

### Preview canvas
- Drawn with WPF (`Canvas` with `Path`/`Line`/`TextBlock`), two views:
    - **Plan** (seen from above): the outer rounded rectangle, the inner wall line, the Length and Width labels.
    - **Vertical section**: the box body (wall and bottom), the lid above with the lip fitting inside, the gap between the lip and the wall, the labels Height, Wall, Bottom, LidThickness, LidLip, LidClearance.
- The scale adapts automatically to fit the frame but **keeps the true proportions** between the dimensions, with no distortion.
- Dimension lines and value labels update as the user types or drags a slider.
- When an input is focused, **highlight the matching dimension** on the canvas, so the user knows which edge this parameter is.
- The wall and the gap are very thin relative to the box, so draw them with a minimum thickness of a few pixels, with a zoom mode for the section area if needed.
- An invalid value: the input gets a red border, the canvas keeps the last valid shape and marks the faulty dimension, the Apply button is disabled.
- The two views can be switched by tab or shown side by side if the window is wide enough.

### Technical principles
- Use **MVVM**: the ViewModel holds the parameter values, the validity state and the commands. The View only binds.
- **The shape computation layer for the canvas is a pure layer** (it takes a parameter set and returns a list of shapes and labels), and does not call Inventor. This keeps the preview smooth, does not make the model recompute on every keystroke, and can be tested without opening Inventor.
- **The canvas is only an illustrative diagram**, not an exact render of Inventor. The real model changes only when Apply is clicked (one Transaction).
- Every Inventor API call runs only on the UI thread of Inventor.
- When the UI opens or Refresh is clicked, read the values again from the model. If the user edits a Parameter directly in Inventor, the UI follows on the next refresh.

### Presets (saved as a JSON file)
- A **preset** is a named set of parameter values (for example Small box, Standard box, Large box). In the UI it is a drop-down list: choosing a preset fills the fields and redraws the canvas right away, and only clicking Apply changes the model.
- Sample file: `presets.json`, with 3 initial presets. All of them satisfy the constraints in section 4.
- **File structure**: `schemaVersion`, `units` (mm), and a list of `presets`, each preset having a `name` and `values` keyed by Parameter name (`BX_Length`, ...). The Parameter name itself is the key so no extra mapping is needed.
- **Storage location**: the user's preset file lives in the add-in's own data folder (for example under `%AppData%`), while the sample file shipped with the add-in is read-only. On first run the sample file is copied to the user folder.
- **Functions**: choose a preset, "Save as a new preset" from the values being entered, overwrite or delete the user's presets. The "Default" button uses the Standard box preset.
- **When loading the file**: check `schemaVersion`; skip unknown keys; a missing parameter takes the default value; a value outside min/max or violating the constraints marks the preset as invalid and it cannot be chosen. A corrupt or unreadable file is reported and the sample preset set is used, without breaking the add-in.
- Read and write with JSON, not `BinaryFormatter`. Write the file through a temporary file and then rename it to avoid corrupting the file if interrupted halfway.
- "Temporary storage" here means a simple way to save for the first version. If sharing with the whole team is needed later, the file can be placed in a shared folder without changing the structure.

### Can be done later
- Apply automatically after the user stops typing for a short time (off by default because each time is one model recompute).
- A 3D or isometric preview in the canvas, or drawing the preview right in the Inventor graphics window.

---

## 5c. A custom language (DSL) to describe Inventor objects

### The idea
SmartAssembly (for Creo) lets users write programs in its own language to automate rule-based design. This add-in takes that **idea**: the object, parameters, rules and build steps are described in a language we design ourselves, and the add-in is only the interpreter and executor inside Inventor. The syntax and semantics are not copied from SmartAssembly.

### Goals
1. One **description file** is the single source for one kind of object: parameters, constraints, derived values, build steps.
2. **The UI and canvas are generated from the description file** (the list of input fields, min/max, error messages), not hand-written for each kind of object.
3. Adding a new kind of object is mostly writing a new description file, with little or no change to the add-in core.
4. Error messages point out the **line and column** in the description file.

### Out of initial scope
- Top-down assembly in the SmartAssembly style (placing many components in an Assembly).
- A dedicated editor with syntax highlighting and code hints.
- Running the language outside Inventor or on the web.

### Processing pipeline
1. **Lexer**: splits the text into tokens, recording the line/column position.
2. **Parser**: builds the syntax tree (AST), reports syntax errors with a position and a fix hint.
3. **Semantic analysis**: checks undeclared or duplicate names, checks types and **units** (no adding mm to degrees), detects circular dependencies among derived values, orders the computation.
4. **Evaluator**: computes expressions with units, checks the rules, returns the list of violations with messages.
5. **Build plan**: a list of abstract operations in order (create sketch, extrude, shell, fillet, name body...), without calling Inventor yet.
6. **Inventor Adapter**: walks the build plan and calls the Inventor API, inside one Transaction.
7. The canvas **preview renderer** reads the same computed result, not going through Inventor.

Every step can be tested **without opening Inventor**; only step 6 needs it.

### Language concepts needed
| Concept | Role | Note |
|-----------|---------|---------|
| Object declaration | Name, description, Parameter prefix | Each file is one kind of object |
| Parameter | Name, type (length, angle, integer, text), unit, default, min/max | This is the table in section 4 |
| Derived value | Computed from other parameters | For example the inner corner radius |
| Rule/constraint | A condition that must hold, with an error message | This is the "Validity constraints" list in section 4 |
| Expression | Arithmetic, comparison, the `min`/`max` functions, units | The core of the language |
| Build step | Sketch, feature, body name | In order, referring to parameters by name |
| Condition, loop (later) | Turn a part on or off, the number of elements by an integer parameter | Needed for ribs, hole arrays, stepped shafts |
| Import/library (later) | Reuse definitions | For a component library |
| Preset | A named set of values | Keep the JSON file, not necessarily put in the DSL |

### Language design decisions (yours to make)
| # | Question | Choice | Reference suggestion |
|---|---------|----------|-----------------|
| D1 | Style | **Direction chosen: line-by-line imperative, upper-case keywords, three blocks (form, tables, build), in the SmartAssembly style** | Mapping details in `smartassembly-to-inventor-mapping.md`. Replaces the hybrid option of the earlier version; the "build plan" step is now the Host interface and the statement trace (section 14 of that file) |
| D2 | External syntax | A custom text / based on JSON or YAML | If you want a truly own language use a custom text; JSON is only a temporary option |
| D3 | How to write the parser | Hand-written (recursive descent) / a library (for example Pidgin, Superpower, Sprache) / ANTLR | Hand-written for a small language gives the best error messages; a library is faster at first |
| D4 | Types and units | Static unit checking / run-time checking only | Check statically, because a wrong unit is the most common error |
| D5 | Names referring to Inventor | Parameter names map straight to Parameter names / an explicit mapping | Map straight, using the prefix declared in the file |
| D6 | File extension and location | **The `.gea` extension (settled)**; a library folder per product | Specified in `gea-language-spec.md`, sample file `tank-level1.gea` |
| D7 | Language version | Whether the file has a `version` | Should be there from the start so the syntax can change later |
| D8 | Error messages | Level of detail, language (Vietnamese/English) | With line, column, an excerpt, a hint |

### DSL development steps
| Step | Content | Done when |
|------|----------|----------------|
| D0 | Write **by hand** a few sample files describing the box in the syntax you want, with no parser yet. Settle the decisions D1-D8 by trying them in writing | There is a complete box description file that feels natural when you read it |
| D1 | Lexer, parser, AST for the parameters and rules. No Inventor needed | It reads the box file and reports syntax errors at the right line and column |
| D2 | Semantic analysis and the units-aware expression evaluator | For a given set of values it returns the right list of violated rules. The UI and canvas can read the definition |
| D3 | The build steps and build plan, the Adapter calling Inventor | The same description file builds a box identical to the hard-coded version of Phase 2 |
| D4 | A second object kind (a flange or a stepped shaft) | It runs from a new file without changing the core. Only now are conditions, loops and integers added |
| D5 | Library, import, better error messages | Common definitions can be reused |

### Relation to the other phases
- **Phase 2 still builds the box with hard-coded code** first. This helps you learn the Inventor API and produces a **reference to compare against**: what the DSL builds must match the hard-coded version (the same Parameters, bodies, volume).
- D0 and D1, D2 can run in parallel with Phase 0-1, because they do not need Inventor.
- `presets.json` stays as it is; the keys are the parameter names declared in the description file.

### Testing the DSL
- **Lexer and parser unit tests**: valid files and many kinds of invalid files, checking the error positions.
- **Semantic tests**: mismatched units, undeclared names, circular dependencies.
- **Rule tests**: given boundary and violating value sets, check the error list.
- **Golden tests**: a description file and a given value set, comparing the build plan with the saved expected result.
- **Comparison with the hard-coded version** in Inventor: the DSL and the hard-coded code give the same model.

---

## 5d. Target object: a power transformer (referencing the GE Vernova image)

### About the object
- The reference image is **one large power transformer** (a piece of equipment in a substation), not a whole substation. The add-in will build a **simplified, parametric model** with a similar overall shape, not a copy of any specific product.
- The image is used only to reference the general form. Do not copy proprietary design details, brands or logos.
- The real dimensions (by rating, voltage class, standard) **come from you, from a catalog or drawings**. This plan only defines the structure and parameter names, and does not set technical figures itself.

### Blocks visible in the image
| Block | Description (from the image, may need adjusting) |
|------|--------------------------------------|
| Tank | A large rectangular block, the wall has stiffening plates and a round inspection cover (manhole); the base below has slots |
| Cover and the ducts on the cover | The cover part is raised as an inclined box running along the tank |
| Ducts (bus duct/turret) | Inclined rectangular blocks connecting from the cover to each bushing |
| Bushings | Many long bushings placed **almost horizontal**, one very long bushing placed inclined, a few small upright ones; the bushing body has sheds |
| Cooling pipes | Large-diameter pipes with flanges, running around and bending along a 3D path, with red valves |
| Separate cooler | A separate group of equipment placed outside the tank, connected by pipes |
| Accessories | Valves, red caps at the foot, a ladder, lifting rings |

### Split into levels (increasing detail)
| Level | Content | How it is held in the file | Note |
|-----|----------|----------------------|---------|
| **1. Tank and cover** | Exactly the hollow box with a cover in section 4, renamed tank and cover; add the skirt | Part, many bodies | Do first, verifies the whole create-and-edit loop |
| **2. Raised cover, ducts, stiffeners** | The inclined raised cover, inclined ducts, stiffening plates, round inspection covers | Part, many bodies | Adds integer parameters (the number of stiffeners) and the tilt angle |
| **3. Bushings** | A parametric family of bushings (diameter, length, number of sheds), placed by position and angle | A Part with many bodies, or an **Assembly** | The number of bushings varies by configuration; make the bushing a reusable part |
| **4. Cooling pipes and valves** | Pipes along a 3D path with bend points, flanges, valves | **Assembly** | The hardest part of the geometry (a sweep along a 3D path) |
| **5. Separate cooler and accessories** | The outside cooling group, ladder, lifting rings, wheels or a skid base | **Assembly** | Use a versioned component library |

### Change from the "Part first" decision
- Levels 1 and 2 are still **one Part with many bodies**, keeping to the current plan.
- From Level 3 onward, switch to an **Assembly** with a component library (bushings, pipes, valves...). This brings the content of Phase 5 forward, but only once Levels 1-2 run stably.
- The add-in, DSL and Adapter must be designed to build **both Parts and Assemblies** from the start (not implemented yet, just not hard-locking the assumption of "one Part").

### Parameters by level (suggested names, fill in values from the catalog)
The prefix `TR_` replaces `BX_` when moving to the transformer. Level 1 maps directly from the table in section 4.

| Level | Parameter name | Meaning | Type | Unit | Old equivalent |
|-----|---------------|---------|------|--------|---------------|
| 1 | TR_TankLength, TR_TankWidth, TR_TankHeight | Outer tank size | Length | mm | BX_Length, BX_Width, BX_Height |
| 1 | TR_Wall, TR_Bottom, TR_CornerRadius | Wall, bottom thickness, corner radius | Length | mm | BX_Wall, BX_Bottom, BX_CornerRadius |
| 1 | TR_CoverThickness, TR_CoverLip, TR_CoverClearance | Cover and cover lip | Length | mm | BX_LidThickness, BX_LidLip, BX_LidClearance |
| 1 | TR_SkirtHeight | Skirt height | Length | mm | new |
| 2 | TR_CoverRiseHeight, TR_CoverRiseAngle | Height and tilt angle of the raised cover | Length, Angle | mm, deg | new |
| 2 | TR_RibCount, TR_RibSpacing, TR_RibDepth | Number, pitch and depth of the stiffening ribs | Integer, Length | -, mm | new |
| 2 | TR_ManholeCount, TR_ManholeDiameter | Number and diameter of the inspection covers | Integer, Length | -, mm | new |
| 3 | TR_BushingCount | Number of bushings | Integer | - | new |
| 3 | TR_BushingDiameter, TR_BushingLength, TR_BushingShedCount | Bushing size | Length, Integer | mm | new |
| 3 | TR_BushingAngle, TR_BushingSpacing | Placement angle and the spacing between bushings | Angle, Length | deg, mm | new |
| 4 | TR_PipeDiameter, TR_PipeBendRadius | Cooling pipe | Length | mm | new |
| 4 | TR_PipeRoute | Pipe route (a list of points) | List | mm | new |
| 5 | TR_CoolerOffset, TR_CoolerSize | Position and size of the cooler | Length | mm | new |

Some parameters are **lists** (the pipe route, the position of each bushing). This affects the UI, the DSL and presets: a single input field is not enough, a table or a list of points is needed. Handle it when reaching Levels 3 and 4, not needed at Level 1.

### Impact on the other parts of the plan
- **Preview canvas**: add a side view with the tank, raised cover and inclined bushings (Levels 2-3). The cooling pipes (Level 4) only need a route diagram.
- **DSL**: needs lists, loops over an integer (the number of bushings, ribs) and conditions earlier than expected, because the bushings and ribs depend on counts. Put these features into step D4 of section 5c.
- **Testing**: add the cases of the smallest and largest number of bushings, the bushing angle at both limits, a pipe route with a bend point, many bushings overlapping.
- **Appearance**: the model only needs to resemble the layout and proportions, not the photo. Skip small details (bolts, weld lines) in the first version.

### Risks specific to this object
| Risk | How to reduce it |
|--------|-----------|
| Too much detail, the project never finishes | Work level by level, move to the next level only when each runs stably; settle "similar enough" for each level |
| A pipe along a 3D path easily fails when sizes change | Use a route precomputed from the parameters, check the bend points and the minimum bend radius |
| Inclined bushings make the ducts and bushings misalign | Compute the duct position from the position and angle of the bushing, do not place by hand |
| Missing real figures | You provide the dimension table from the catalog; leave placeholders in the parameter table until then |
| A single-file Part is too heavy with many bushings | Switch to an Assembly from Level 3, using a repeated bushing part |

---

## 6. Testing (manual)

| Situation | Expected |
|------------|---------|
| Create the object with default values | The model has the right dimensions |
| Change each parameter to its min and max | The model updates, no error |
| Enter a value outside the allowed range | The UI blocks it, the model does not change |
| Enter a value that makes the geometry impossible | Undo, a clear message |
| Ctrl+Z after an edit | Goes back to exactly the previous state |
| Save, close, reopen, then keep editing | The add-in still recognizes the object |
| Open an ordinary Inventor file not created by the add-in | The add-in reports no error, it only disables the edit function |
| Click create when no document is open | A clear message, no error |
| Click create while an Assembly or Drawing is open | A message that only Part is supported |
| Click create a second time on a Part that already has a box | No duplicate is created, switches to parameter editing |
| Click create on a Part that has someone else's geometry or `BX_` Parameters | A warning, no overwrite |
| Creation fails halfway | The Part returns exactly to its original state |
| Type or drag the slider for each parameter | The canvas changes at once, the Inventor model does not change until Apply is clicked |
| Focus an input field | The matching dimension is highlighted on the canvas |
| Set each parameter to its min and max | The canvas still displays clearly, the picture does not break |
| Enter a value that violates a constraint (e.g. Wall too large) | The field turns red, the Apply button is locked, the canvas does not draw a wrong shape |
| Stretch or shrink the dock window | The canvas scales in proportion, not distorted |
| Switch the light/dark theme of Inventor | The UI and canvas are still readable |
| Close Inventor while the add-in is open | Exits cleanly, no hung process |

---

## 7. Risks and how to reduce them

| Risk | How to reduce it |
|--------|-----------|
| The sketch loses constraints or flips when parameters change | Fully constrain when creating, test at the boundary values |
| Wrong units (cm versus mm) | Always use an expression with a unit, centralize the conversion in one place |
| The add-in does not load on another machine | Settle the Inventor version, check the `.addin` file and the paths |
| A COM leak stops Inventor from exiting | Rules for releasing references, test closing the app |
| The user edits a Parameter and the state drifts | Read the values from the model each time the UI opens, do not cache for long |
| Hosting WPF in the Inventor dockable window gives display or focus problems | Try an empty UserControl already in Phase 1, before building the form and canvas |
| The canvas differs from the real Inventor shape and misleads users | Label it clearly as an illustrative diagram, draw to proportion, do a 3D preview only after the 2D version is stable |
| Typing each key recomputes the model many times, the UI stutters | The canvas uses the pure shape computation layer, and only pushes into Inventor when Apply is clicked |
| The DSL grows into a project of its own, and the add-in never runs | Build the box with hard-coded code first (Phase 2); add language features only when a real example needs them |
| Designing the syntax too early and then having to change it, breaking files already written | D0 writes sample files by hand before the parser; the file has a `version` |
| Confusing error messages leave the description file writer stuck | The lexer records line and column from the start, test error cases separately |
| A wrong unit in an expression | Static unit checking in the semantic analysis step |
| The DSL builds something different from the hard-coded version and nobody notices | Compare the Parameters, bodies and volume of the two versions in Inventor |

---

## 8. Next things to do right away

The decisions in section 2 and the parameter table in section 4 are done. Two branches can run in parallel:

**The Inventor branch**
1. Phase 0: install Visual Studio 2026, the Inventor 2027 SDK, create the solution.
2. Phase 1: the add-in skeleton, ribbon, a dockable window holding an empty WPF UserControl.
3. Phase 2: build the box with hard-coded code on the open Part (make the reference).

**The language branch (no Inventor needed)**
1. D0: write by hand the box description file in the syntax you want, settle the decisions D1-D8 in section 5c.
2. D1, D2: lexer, parser, semantic analysis, the units-aware expression evaluator.
3. D3: the build plan and the Adapter, compare with the hard-coded version.
