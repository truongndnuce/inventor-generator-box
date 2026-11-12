# Design: general-purpose builders for `.gea` (towards "any machine")

Date: 2026-11-11. Status: draft for review. Companion of `2026-11-11-gea-designer-design.md` (section 13 of that document points here). Language reference: `sa-docs/gea-language-spec.md`; builder manifest format: its section 12.

## Goal

A new machine type (a fan, a pump, a gearbox housing…) must be describable in a `.gea` script **without writing C#**. Today that is not true: the six builders (Tank, Cover, Block, Cylinder, Bushing, Radiator) are shaped like transformer parts, so a script can only assemble boxes, plain cylinders and transformer-like parts. This design replaces "one builder per product part" with a small set of **general modelling operations** that any machine can be composed from, and keeps today's builders as shortcuts.

Concrete test of success: an axial fan with twisted blades, a volute casing and a bolted flange can be written entirely in `.gea` (section 10), and so can everything `generator.gea` and `turbine.gea` do today.

## Out of scope

- Assemblies (`TARGET ASSEMBLY`, mates, `ADD_COMPONENT`): still reserved (see `docs/component-reuse-roadmap.md` direction C). All builders here make **bodies inside one Part**.
- Surfacing, sheet metal, weldments, FEA, iLogic, drawing changes.
- Importing existing CAD files as bodies.
- Changes to the Designer UI: it picks new builders up from the manifests (`GET /api/gea/builders`).

## 1. Current state (what a new builder has to fit into)

Verified in the code:

- **Manifest**: `GeaBuilderCatalog.All` (`Gea/GeaManifests.cs`) lists each builder's inputs and outputs (`BuilderPort(Name, Type, Required, Default, Description)`). The semantic checker uses it to validate `CREATE_FEATURE` without Inventor; the server compiles this same file (linked in `Cadflow.Api.csproj`).
- **Inventor implementation**: one big `switch (builder.Name)` in `InventorGeaHost.CreateFeature` (`Services/Gea/InventorGeaHost.cs:238-352`). Each case validates inputs with `Require(...)` (failing with `GEA4099`), calls a geometry helper in `PartGeometry`, and returns a `GeaRef("BODY", built, builder.Name, props)`. An unknown name fails with `GEA2013`.
- **Geometry helpers**: `PartGeometry` (`Services/Gea/PartGeometry.cs`): sketches on offset work planes, extrude/revolve helpers, coordinates `(x, h, d)` in mm converted to cm for the API. Every sketch, feature and plane created is passed to `_track` so the next *Apply* deletes exactly what the script created and rebuilds it.
- **Simulation Host**: `GeaSimulationHost.CreateFeature` (`Gea/GeaHost.cs`) is generic: it records a trace and returns virtual bodies; only `Bushing.Turret` has special handling. It reproduces no geometry.
- **Values and references**: `GeaType` = Integer, Double, Bool, String, Length, Angle, Reference, Unknown. `GeaRef` has a `Kind` (only `"BODY"` is used) and a `Props` dictionary the builder fills so later builders can read dimensions (`Cover` reads `Tank.Props`). There is **no list/array type and no point/vector type**.
- **Orientation**: the fixed convention is `X` along the length, `D` along the depth (Inventor `Z`), elevation along Inventor `Y`. `Cylinder` has an `Axis` of `"D"`, `"X"` or `"H"`. Nothing can be rotated about an arbitrary axis.
- **Math**: expression functions are `MIN, MAX, ABS, ROUND, FLOOR, CEIL, SQRT, STR, NUM, FIND` (language spec section 10). **There is no `SIN`, `COS`, `TAN`, `ATAN2`, `POW`**, which blade profiles and any computed circular layout need.
- `IGeaHost.CreateFeature` already carries an `on` reference parameter next to the inputs; I did not find a statement that sets it today, so treat it as unused.

## 2. Principles

1. **One extension point stays**: manifest in the catalog + a case in the Host + a simulation behaviour. A builder is not accepted without all three and a test.
2. **Compose, don't special-case**: a product-shaped builder may exist only if it can be written as a library `.gea` (`INCLUDE lib:`) over the general ones. New machines go through libraries, not through C#.
3. **Everything validated before Inventor is touched**: input checks in the manifest and in `Require(...)`, so errors name the builder and the input (as `Tank:` does today).
4. **Rebuild-safe**: every sketch, plane and feature a builder creates goes through `_track`, so *Apply* twice gives the same model.
5. **Deterministic**: no dependence on selection order or UI state. Where an operation needs an edge or face, the script describes it by a rule (section 8), never by a click.
6. **Units**: lengths in mm and angles in degrees at the script level, as now.

## 3. Orientation: the Frame

The existing `X / D / Elevation` system only places axis-aligned bodies. General parts need a local coordinate system. Introduce a reference kind **`FRAME`** (origin + three orthogonal axes).

- The **world frame** is the current convention (`X`, `D`, elevation), so every existing script means the same thing. It is the default of any `Frame` input.
- Builder `Frame` creates one:

| Input | Meaning |
|---|---|
| `Parent` (Frame, optional) | Frame to start from; default world |
| `OriginX`, `OriginD`, `Elevation` (Length) | Origin, in the parent's coordinates |
| `Axis` (String: `"H"`, `"X"`, `"D"`) | Snap the new frame's local Z to that parent axis (what `Cylinder.Axis` does today) |
| `RotX`, `RotD`, `RotH` (Angle) | Extra rotations about the parent axes, applied in this fixed order after `Axis` |

  Output `Frame`. Frames are pure values (no model objects), so they cost nothing and are not tracked.
- All general builders take an optional `Frame` input; every position and every profile coordinate is **local to that frame**. Local Z is the "extrusion direction", local XY is the sketch plane.
- Inventor side: a frame becomes a construction work plane (fixed plane through the origin with the frame's X and Y directions) plus an axis, created through `PartGeometry` and tracked. **To verify in the Windows spike (section 12)**: `WorkPlanes.AddFixed` and how a sketch on it maps points (existing code already does this indirectly with `ModelToSketchSpace`).

## 4. Profiles

A 2D profile is the input of Extrude, Revolve, Loft and Sweep. New reference kind **`PROFILE`**; a profile belongs to a frame (its sketch plane) and records its closed/open state.

Two ways to make one, delivered in two steps:

**Step 1, no language change: standard profile builders**, all with `Frame` (optional) and `CenterX`, `CenterY` local offsets:

| Builder | Inputs |
|---|---|
| `RectProfile` | `SizeX`, `SizeY`, `CornerRadius` |
| `CircleProfile` | `Diameter` |
| `EllipseProfile` | `DiameterX`, `DiameterY` |
| `SlotProfile` | `Length`, `Width` (stadium shape) |
| `PolygonProfile` | `Sides`, `Diameter` (regular polygon, circumscribed) |

**Step 2, language change: an arbitrary profile block** (needed for airfoil sections, gear teeth, brackets):

```
BEGIN_PROFILE section FRAME f        ! closed profile; use OPEN for a path
  MOVE_TO  0 mm 0 mm
  LINE_TO  120 mm 0 mm
  ARC_TO   130 mm 12 mm  RADIUS 40 mm
  SPLINE_THROUGH x1 y1  x2 y2 ...    ! (open decision, section 14)
  CLOSE
END_PROFILE
```

Statements inside may be inside `FOR`/`IF`, which is how a computed point list (an airfoil from a formula) is written. An `OPEN` profile is a **path** for Sweep. This adds statements to the grammar, the parser, the interpreter, `IGeaHost` (a `CreateProfile` method) and both Hosts, so it is the second step.

## 5. Language additions

| Addition | Why | Where |
|---|---|---|
| `SIN(a)`, `COS(a)`, `TAN(a)` (argument `ANGLE`, result `DOUBLE`), `ATAN2(y, x)` (result `ANGLE`), `POW(a, b)`, `PI` | Computed layouts, airfoil formulas, helix pitch | `GeaCore` function table + type rules; language spec section 10 |
| `DECLARE_REFERENCE_SET s` and `SET_ADD s ref` | A loop that collects profiles for a loft, or bodies for a combine, without a list type | Parser, interpreter; the value is a `GeaRef` of kind `SET` |
| Profile block (section 4, step 2) | Arbitrary 2D shapes | Parser, interpreter, Host interface |
| New reference kinds `FRAME`, `PROFILE`, `SET` | Orientation, shapes, groups of bodies | `GeaRef.Kind`; the semantic checker must check kinds at `FEATURE_IN` (a `Reference` port may say which kinds it accepts) |

All additions need entries in the language spec (sections 9, 10, 15) and checker diagnostics.

## 6. The builders

All names are proposals. Output port is `Body` (kind `BODY`) unless stated.

### Wave 1: enough for a fan, a pump housing and the existing products

| Builder | Inputs (R = required) | Output | Notes |
|---|---|---|---|
| `Frame` | section 3 | `Frame` | |
| `RectProfile` … `PolygonProfile` | section 4 | `Profile` | |
| `Extrude` | `Profile` R, `Distance` R, `Direction` (`"UP"`/`"DOWN"`/`"BOTH"`), `Operation` (`"NEW"`/`"JOIN"`/`"CUT"`/`"INTERSECT"`), `Target` (Body, needed for JOIN/CUT/INTERSECT), `TaperAngle` | `Body` | The workhorse. `CUT` with a `Target` replaces today's `Pocket` helper |
| `Revolve` | `Profile` R, `Angle` (default 360), `AxisSide` (which profile edge/axis: `"X"` or `"Y"` of the local frame), `Operation`, `Target` | `Body` | Hubs, casings, shafts, nozzles |
| `CircularPattern` | `Source` (Body) R, `Count` R, `Angle` (default 360), `AxisFrame` (Frame whose local Z is the axis) | `Bodies` (kind `SET`) | Blades, bolt circles. One call, not a `FOR` of N rebuilds |
| `LinearPattern` | `Source` R, `Count` R, `Spacing` R, `Frame` (direction = local X) | `Bodies` | Fins, ribs, studs |
| `Transform` | `Body` R, `OffsetX`, `OffsetD`, `OffsetH`, `RotX`, `RotD`, `RotH`, `Copy` (Bool) | `Body` | Moves/rotates a body (also a set); `Copy=true` keeps the original |
| `Mirror` | `Body` R, `PlaneFrame` R (mirror plane = local XY), `Copy` | `Body` | Symmetric halves |
| `Combine` | `Target` R, `Tools` R (Body or Set), `Operation` R (`"JOIN"`/`"CUT"`/`"INTERSECT"`), `KeepTools` | `Body` | Hollow casings, openings, blade-to-hub joins |

### Wave 2: realism

| Builder | Inputs | Notes |
|---|---|---|
| `Loft` | `Sections` (Set of Profiles) R, `Operation`, `Target`, `Closed` | Twisted blades, transitions. Needs the Set statements |
| `Sweep` | `Profile` R, `Path` (open Profile) R, `Operation`, `Target`, `TwistAngle` | Pipes, bends, volutes |
| `Fillet` / `Chamfer` | `Body` R, `Radius`/`Distance` R, `Edges` (selector, section 8) R | |
| `Hole` | `Target` R, `Frame` R (hole axis = local Z), `Diameter` R, `Depth` (0 = through), `CounterboreDiameter`, `CounterboreDepth` | Bolt holes in flanges |
| `Shell` | `Body` R, `Thickness` R, `OpenFaces` (selector) | Thin-walled housings |
| `Helix` / thread-like | later, only on demand | |

Today's builders stay as they are. `Tank`, `Cover`, `Block`, `Cylinder` could later be re-expressed as libraries over `Extrude`/`Combine` to prove the general set is complete, but that is a refactor with its own risk and not part of this work.

## 7. Sets of bodies

A pattern produces many bodies. `GeaRef` kind `SET` holds an ordered list of body refs. Rules:

- `Combine` accepts a Body or a Set in `Tools`; `Transform` and `Mirror` accept both and return the same kind.
- `SET_REF_NAME` on a set applies the numbering rule to each member (`"FN_Blade*"` → `FN_Blade_1`, `FN_Blade_2`…), `SET_REF_ATTR` sets the attribute on each.
- `Count` of members is readable (`COUNT(set)`, one more expression function; added with the Set statements).
- The simulation Host represents a set as N virtual bodies.

## 8. Edges and faces: describe by rule, never by click

`Fillet`, `Chamfer`, `Shell` and `Hole` need to pick edges or faces. A selector is a **string rule** evaluated on the body when the builder runs, with a small fixed vocabulary:

| Selector | Meaning |
|---|---|
| `"ALL"` | every edge of the body |
| `"TOP_FACE"`, `"BOTTOM_FACE"`, `"SIDE_FACES"` (relative to the body's frame Z) | faces at the extremes / around the sides |
| `"VERTICAL"`, `"HORIZONTAL"`, `"CIRCULAR"` | edges by orientation or type |
| `"EDGES_OF:TOP_FACE"` | edges bounding a face selector |
| `"WITH_ATTR:<key>=<value>"` | faces/edges of features that carry the attribute set with `SET_REF_ATTR` |

This is deliberately small. It covers the common cases (round all edges, shell open at the top, chamfer the rim). Anything beyond it is an explicit request for a new selector, not a free-form query language. **Open decision** (section 14): whether this vocabulary is enough for the first machines.

## 9. Simulation Host and the checker

- Each new builder gets its manifest, so `GeaChecker` validates its inputs and the kinds of references (a `Loft` given a Body is an error), without Inventor.
- The simulation Host (`GeaSimulationHost`) must, for each builder, return the right **kind** of output (`FRAME`, `PROFILE`, `SET` with N members for patterns), validate the same `Require` conditions that are checkable without geometry (counts, positive sizes), and keep an approximate **bounding box** per body where the inputs allow it (extrude: profile bounds + distance; revolve: bounds of the revolved profile; transform: moved box). This makes the dry run in the Designer useful and gives tests something to assert, but it is **not** a geometry check: only Inventor can say a loft is valid.
- Errors raised by the Inventor Host use `GEA4099` with the builder name and input, as today.

## 10. Worked example (illustrative syntax, not final)

An axial fan: casing ring with flange holes, hub, motor, N twisted blades from airfoil-like sections.

```
! Casing: revolved ring
CREATE_FEATURE builder:Frame
  FEATURE_IN "Axis" "X"
  FEATURE_IN "Elevation" FN_AxisHeight
  FEATURE_OUT axis "Frame"
END_CREATE_FEATURE

CREATE_FEATURE builder:CircleProfile
  FEATURE_IN "Frame" axis
  FEATURE_IN "Diameter" FN_CasingDiameter + 2 * FN_Wall
  FEATURE_OUT outer "Profile"
END_CREATE_FEATURE
CREATE_FEATURE builder:Extrude   ! outer cylinder
  FEATURE_IN "Profile" outer
  FEATURE_IN "Distance" FN_CasingLength
  FEATURE_OUT casing "Body"
END_CREATE_FEATURE
CREATE_FEATURE builder:CircleProfile
  FEATURE_IN "Frame" axis
  FEATURE_IN "Diameter" FN_CasingDiameter
  FEATURE_OUT bore "Profile"
END_CREATE_FEATURE
CREATE_FEATURE builder:Extrude   ! cut the bore
  FEATURE_IN "Profile" bore
  FEATURE_IN "Distance" FN_CasingLength
  FEATURE_IN "Operation" "CUT"
  FEATURE_IN "Target" casing
  FEATURE_OUT casing "Body"
END_CREATE_FEATURE

! Hub
CREATE_FEATURE builder:CircleProfile ... FEATURE_OUT hubP "Profile"
CREATE_FEATURE builder:Extrude ...      FEATURE_OUT hub "Body"

! One blade: lofted through sections at increasing radius, each rotated more (twist)
DECLARE_REFERENCE_SET sections
FOR k FROM 0 TO FN_SectionCount - 1
  DECLARE_VARIABLE LENGTH r
  DECLARE_VARIABLE ANGLE  tw
  r  = FN_HubDiameter / 2 + k * (FN_BladeLength / (FN_SectionCount - 1))
  tw = FN_RootTwist - k * (FN_RootTwist - FN_TipTwist) / (FN_SectionCount - 1)
  CREATE_FEATURE builder:Frame            ! local frame at radius r, rotated by tw
    FEATURE_IN "Parent" axis
    FEATURE_IN "OriginD" r
    FEATURE_IN "RotX" tw
    FEATURE_OUT f "Frame"
  END_CREATE_FEATURE
  BEGIN_PROFILE s FRAME f                 ! airfoil points from a formula: needs SIN/COS/POW and a nested FOR
    ...
  END_PROFILE
  SET_ADD sections s
END_FOR
CREATE_FEATURE builder:Loft
  FEATURE_IN "Sections" sections
  FEATURE_OUT blade "Body"
END_CREATE_FEATURE

! N blades around the axis, joined to the hub
CREATE_FEATURE builder:CircularPattern
  FEATURE_IN "Source" blade
  FEATURE_IN "Count" FN_BladeCount
  FEATURE_IN "AxisFrame" axis
  FEATURE_OUT blades "Bodies"
END_CREATE_FEATURE
CREATE_FEATURE builder:Combine
  FEATURE_IN "Target" hub
  FEATURE_IN "Tools" blades
  FEATURE_IN "Operation" "JOIN"
  FEATURE_OUT rotor "Body"
END_CREATE_FEATURE
```

Everything above uses only Wave 1 and Wave 2 builders plus the language additions in section 5. A flange with bolt holes is `Extrude` + `Hole` + `CircularPattern`; a volute is `Sweep` with a growing section or a `Loft`.

## 11. Testing

The development machine has no Inventor, so tests split in two:

**On any machine (`be/Cadflow.Api.Tests`)**
- Manifest tests: every new builder is in `GeaBuilderCatalog.All`; port names/types are unique; `GET /api/gea/builders` lists them.
- Checker tests: for each builder, a valid call compiles; a missing required input, a wrong reference kind and an unknown port each produce the expected diagnostic.
- Language tests: the new math functions (value, units/type rules, domain errors), `DECLARE_REFERENCE_SET`/`SET_ADD`, the profile block (parser, nesting in `FOR`/`IF`, closed vs `OPEN`).
- Simulation tests: a script with each builder runs in `GeaSimulationHost`, produces the expected kinds and counts (a pattern of N gives N bodies), and the bounding boxes are as expected.
- Regression: the shipped scripts (`box`, `transformer`, `turbine`, `generator`) still compile and dry-run to the same trace as before.

**On the Windows machine with Inventor 2027 (manual checklist, one per builder)**
- Build the builder's reference script; compare **volume, bounding box and body count** to expected values (a table in the test notes), and the picture by eye.
- *Apply* twice: the model is identical (ownership/`_track` rebuild works).
- Export STEP and confirm it opens.
- Run it once through the worker with a job (`kind=preview`).

Automating the Windows side (a headless Inventor test project) is worth considering once there are more than a handful of builders; it is not part of this design.

## 12. Phase 0: a feasibility spike on Windows (before any builder is written)

The Inventor API calls below are the ones I expect these builders to use; **I could not verify them on this machine**, so confirm each in a throwaway add-in before committing to the design:

1. A fixed work plane from a frame (origin + X/Y directions), and a sketch on it with points mapped correctly.
2. `ExtrudeFeatures` with `NewBody` / `Join` / `Cut` / `Intersect`, and extrude from a profile on that plane, both directions, taper.
3. `RevolveFeatures` about a sketch axis.
4. Circular and rectangular **pattern of a body (solid)**, not only of a feature: confirm Inventor can pattern a solid body and yields one body per instance, and how those are named/tracked.
5. `LoftFeatures` through ≥ 3 sections on non-parallel planes (the twisted-blade case) and `SweepFeatures` along a 3D/2D path.
6. Moving, rotating and mirroring a body (`MoveFeatures` / `MirrorFeatures`) and `CombineFeatures`.
7. Selecting edges and faces by rule (orientation, type, relative position) for fillet, chamfer, shell and hole.

For each, record: works / works with caveat / not available, the exact calls, and the failure modes. The outcome can change the builder list (for example if a body pattern is not available, `CircularPattern` becomes a loop of `Transform` copies with the same manifest).

## 13. Phases

| Phase | Content | Needs Inventor |
|---|---|---|
| 0 | Feasibility spike (section 12); decide the frame implementation and the pattern strategy | Yes |
| 1 | Language: math functions, `FRAME`/`PROFILE`/`SET` kinds with kind-checking, `DECLARE_REFERENCE_SET`/`SET_ADD`; manifests and simulation Host for `Frame`, standard profiles, `Extrude`, `Revolve`, `Transform`, `Combine` | Only for the Inventor cases |
| 2 | Inventor Host cases for the phase-1 builders; Windows checklist; `CircularPattern`, `LinearPattern`, `Mirror` | Yes |
| 3 | Profile block, `Loft`, `Sweep` | Yes |
| 4 | `Fillet`, `Chamfer`, `Hole`, `Shell` with selectors (section 8) | Yes |
| 5 | Library `.gea` files for common sub-assemblies of parts (flange with bolt circle, bearing housing, blade row) built on the general builders; re-express one existing product (for example `generator.gea`) over them as the completeness proof | Yes |

The parts of phases 1-3 that do not touch Inventor (language, manifests, checker, simulation, tests) can be developed and tested on this machine while the Windows side catches up.

## 14. Risks and open decisions

1. **Everything geometric depends on the spike.** Until section 12 is done, the builder list is a plan, not a commitment, and the Inventor behaviour of patterns, lofts and selections is unverified.
2. **Edge/face selection** (section 8) is the part most likely to be too small or too fragile. Decide after the first real machines are modelled whether the vocabulary needs to grow.
3. **Splines** in the profile block: a spline through computed points is what an airfoil needs, but it increases the surface of the language (point lists in a statement). Alternative for the first fan: a polyline of many points. Decide in phase 3.
4. **Frame rotation order.** A fixed Euler order is simple but surprising for authors (rotations do not commute). The alternative, a frame given by a direction vector plus a hint, needs a vector type. Decide in phase 1 with an authoring test (can someone who does not know the convention place a blade correctly?).
5. **Performance.** A loft through many sections or a pattern of hundreds of bodies can be slow in Inventor; the worker's time limit for a job (`JobWorker`) and the 1,000,000-statement interpreter limit may need review.
6. **Rebuild identity.** Features that depend on earlier features (a pattern whose source is later edited) must still be deleted and rebuilt cleanly by *Apply*; each builder's tracking must be checked by the "Apply twice" test.
7. **More language than the Designer can show.** Frames, sets and profile blocks are harder to present than a list of parameters. The Designer will first expose the standard profile builders and `Extrude`/`Revolve`/patterns/`Combine`; the profile block opens as a text-edit step until it has its own editor.
8. **Re-expressing existing builders** (`Tank` as `Extrude`+`Combine`) would prove completeness but risks regressions in working products; it is deferred to phase 5 and only for a product with a Windows regression check.
