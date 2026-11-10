# Mapping: a SmartAssembly-style language → our own language that runs on Inventor and the web

This document is based on the SmartAssembly language description table you supplied (sections 4.1 to 4.12). Purpose: when the product author **types statements in the SmartAssembly style**, the system understands and executes them on **Inventor (worker)** and on the **web (form, preview, dry run)**.

It continues `inventor-addin-plan.md` (section 5c, DSL) and `cadflow-order-to-model-plan.md` (section 15, web).

---

> **The `.gea` file extension is settled.** The official syntax, types, units and the behavior of each statement are in `gea-language-spec.md`; the sample file is `tank-level1.gea`. If an example in section 15 of this document differs, **the `.gea` specification is the standard**.

## 1. Principles

1. **Keep the style**: imperative, one statement per line, upper-case keywords, no parenthesized function-call syntax, three blocks (interface, tables, main logic).
2. **Keep the idea, name things ourselves when needed**: the common statements (IF, FOR, DECLARE_VARIABLE...) keep similar names so they feel familiar. Statements tied to Creo entities (UDF, feature, trail file) are replaced by the equivalent Inventor concept and given their own names. It is better to name the keywords ourselves and write our own documentation instead of copying Sigmaxim's documentation as it is; if there is any intention to commercialize, consider the terms and trademarks further.
3. **The script only orchestrates**: as your table remarks, the technical value lives in the **component library** (UDFs in Creo). For Inventor, that library is parts, iParts, iFeatures, sample assemblies and the build functions written in C#. Without a library the language has little value (see section 7).
4. **The person ordering does not write code**: the user on the web only fills values into the form. Only the (trusted) product author writes scripts.

---

## 2. Fundamental differences between SmartAssembly and this system

| Aspect | SmartAssembly | This system | Consequence for the language |
|------|---------------|--------------|----------------------|
| Where it runs | Inside Creo, on the open model, with the user sitting at the machine | On a worker (Inventor) with no one watching; the form is on the web | No interactive dialogs on the worker; all input must be done **before** running |
| Interface | The GUI block shows inside Creo | The GUI block becomes a **web form** | It must define when the form is recomputed |
| Picking directly in the model (USER_SELECT) | Yes | No (the person ordering does not see the model) | Needs a replacement or a limit to the interactive mode |
| Messages (MESSAGE_BOX) | A dialog | The web or the job log | The behavior must be defined for when no one is there |
| Entity references | A first-class data type, easily invalidated after a regenerate | The same problem in Inventor | Should recover references automatically, reducing the most common error group |
| Library | The company's UDFs | Parts, iParts, iFeatures, C# builders | A plan to build the library is needed |
| Dry run | Runs in Creo | A dry run **without Inventor** is needed on the web | Needs a simulated run (section 14) |

---

## 3. File structure: three blocks

| SmartAssembly block | Required | Role in this system | Suggested block name |
|--------------------|----------|-----------------------------|------------------|
| `BEGIN_GUI_DESCR ... END_GUI_DESCR` | No | Describes the **web form**: input fields, choice boxes, show or hide by choice | `BEGIN_FORM ... END_FORM` |
| `BEGIN_TAB_DESCR ... END_TAB_DESCR` | No | Configures **tables**: catalog, BOM, price table | `BEGIN_TABLES ... END_TABLES` |
| `BEGIN_ASM_DESCR ... END_ASM_DESCR` | Yes | The **build logic** that runs on the worker | `BEGIN_BUILD ... END_BUILD` |

Rules:
- Comments: `!` for one line, `/* ... */` for several lines (kept as they are).
- Indenting with Tab is only for readability, it has no syntactic meaning.
- File encoding: **UTF-8** (not windows-1252) so that non-ASCII text can be written in messages.
- Declare variables at the top of a block, grouped by type as the table recommends.
- The FORM block runs on the **web and the server** (showing fields, checks), the BUILD block runs on the **worker**.

---

## 4. Data types and declarations

| SmartAssembly | Suggestion | How to implement |
|---------------|---------|----------------|
| `STRING`, `INTEGER`, `DOUBLE`, `BOOL` | Keep | The matching .NET types |
| (none) | **Add `LENGTH`, `ANGLE`** (numbers with units) | Static unit checking (decision D4); avoids mixing up mm and cm (the internal unit of the API is cm and radians) |
| `DECLARE_VARIABLE type name [value]` | Keep | |
| `REFERENCE` / `DECLARE_REFERENCE name` | Keep, but pointing to an Inventor object | Feature, body, face, edge, occurrence, work plane, parameter (see section 6) |
| `ARRAY` (`ADD_ARRAY_ELEM`, `GET_ARRAY_SIZE`, `CLEAR_ARRAY`) | Keep | A .NET list. Needed for the list of bushings, the list of pipe points |
| `MAP` (`DECLARE_MAP`, `.key`, `.value`) | Keep | A dictionary, used for lookup tables |
| `STRUCT` (`POINT`, `VECTOR`, `PDF_OPTION`) | Keep `POINT`, `VECTOR`; rename `PDF_OPTION` to `EXPORT_OPTION` | Maps to the Inventor point and vector, and `NameValueMap` for translator options |
| `STOPWATCH` | Keep | Times each step, goes into the job log |

---

## 5. Control flow

| SmartAssembly | Suggestion | Note |
|---------------|---------|---------|
| `IF / ELSE_IF / ELSE / END_IF` | Keep | |
| `AND OR NOT == <> < <=` (and `>`, `>=`) | Keep, add `>` and `>=` if missing | Comparing numbers with units must use the same unit or one that can be converted |
| `FOR x REF ARRAY / REF MAP ... END_FOR` | Keep | |
| `WHILE ... END_WHILE`, `BREAK`, `CONTINUE`, `i++`, `i--` | Keep | Add a **maximum iteration limit** so a job does not run forever |
| `RETURN` | Keep | Finishes early, the job still succeeds if there was no error |
| Helper functions (converting strings and numbers, absolute value, finding a substring) | Keep, add `MIN`, `MAX`, `ROUND`, `SQRT`, `ABS` | Used in expressions |
| Do not delete elements while iterating an array | **Blocked by the interpreter**: iterate over a copy or report a clear error | The system handles it itself, so the author does not hit confusing errors |

---

## 6. The reference model (the hardest part)

| SmartAssembly | Equivalent in Inventor | Suggestion |
|---------------|-----------------------------|---------|
| `SEARCH_MDL_REF` | Finds one object in the model | Keep the name. Search by **kind** (body, feature, face, edge, work plane, occurrence), a **name with wildcards**, or an **attached attribute** |
| `SEARCH_MDL_REFS` | Finds many objects into an array | Keep the name |
| `SET_FEAT_PARAM`, `SET_REF_PARAM` (attach a parameter to a feature or edge to find it again) | **AttributeSets** attached to a feature, face, edge, body (stored in the file, still there after reopening) | Keep the idea, rename to `SET_REF_ATTR`; find with `SEARCH_MDL_REFS ... ATTR` |
| `REF_VALID`, `REF_EQUAL`, `IS_FAILED`, `PARAM_VALID` | Check that an object is still valid, compare two objects, check whether the last statement failed | Keep the names |
| `INVALIDATE_REF` then search again after a regenerate | An object may become invalid after the model updates | **Automate it**: store the reference as a durable key (the Inventor reference key) or by attribute, and re-bind it on use; still keep `INVALIDATE_REF` for anyone who wants to do it by hand |
| `COPY_REF`, `SET_REF_NAME` (the `*` character makes a unique name), `GET_REF_NAME` | Rename a feature, body, occurrence | Keep the names and the `*` rule |

Notes:
- The exact names of the API parts (reference key, attribute sets) must be checked again against the Inventor 2027 documentation when doing the work.
- **Automatic reference recovery** is a big improvement over SmartAssembly, because your table says this is the most common error group. In return it adds work for the interpreter and may be slower; it can be done after Level 2.
- Prefer **labeling with attributes right at creation** (a role: `TANK`, `COVER`, `BUSHING_1`...) instead of searching by a name that is easily changed.

---

## 7. Building the model: the equivalent of a UDF

SmartAssembly does not draw each feature but calls a ready-made UDF, passing references in and getting references out. The equivalents in Inventor:

| Approach | Description | Pros | Cons | Use when |
|------|-------|-----|-------|----------|
| **iFeature** | A packaged, reusable feature (the closest to a UDF) | Engineers author it themselves in the Inventor UI, no code | Hard to test automatically, file version management | Small repeated details (holes, ribs, slots) |
| **iPart / iAssembly** | A family of parts or assemblies by a parameter table | A variant table is ready-made | The number of variants is fixed by the table | Families of bushings, valves, flanges by voltage class |
| **A sample part or assembly with Parameters** | A template file, inserted then given Parameters | Simple, visual | Each variant is one file update | Large components (cooler, oil tank) |
| **A C# build function (builder)** | Code that builds sketches, features, assemblies, called from the script | Good testing and version management, independent of files | Code must be written for each component | Tank, cover, simple geometric parts (Levels 1-2); start with this |

Suggested statements:

| SmartAssembly | Suggestion | Meaning |
|---------------|---------|---------|
| `CREATE_UDF <path> <model> [options] <group> ... END_CREATE_UDF` | `CREATE_FEATURE <source> ... END_CREATE_FEATURE` (the source can be `lib:` an iFeature, or `builder:` a C# function) | Creates a feature or body |
| `UDF_REF "name" <ref>` | `FEATURE_IN "name" <ref>` | Passes an input reference |
| `UDF_EXP_REF <variable> FEATURE <index>` | `FEATURE_OUT <variable> "name"` | Gets an output reference (by **name**, not by index, so it breaks less when the library changes) |
| `UNGROUP_FEATURES` | Not needed | Builders and iFeatures return objects that already have names |
| `SET_REF_NAME` | Keep | Sets the name |
| `GET_FEATURE_DIMS`, `GET_DIM_VALUE`, `SET_DIM_VALUE`, `SET_DIM_SYMBOL` | `GET_PARAM`, `SET_PARAM` (by **Parameter name**), `SET_PARAM_EXPR` | Uses ModelParameters and UserParameters; control by a name with a unit, not by a dimension index |
| `ASSEMBLE` with constraints | `ADD_COMPONENT <source> <variable>` then a block `ASSEMBLE ... END_ASSEMBLE` with `MATE`, `FLUSH`, `INSERT`, `ANGLE` | Inserts an occurrence and assigns constraints |
| `REGEN_MDL` | `REGEN_MDL` | Updates the model (calls the document update command) |

**Plan to build the library** (without a library the language has nothing to orchestrate):
- Levels 1-2: C# build functions for the tank, cover, ribs, inspection cover (already designed in the add-in).
- Level 3: the bushing family as an iPart or a parametric builder.
- Levels 4-5: pipes, valves, cooler as sample assemblies and a builder for the pipe route.
- Each component has a **name, version, list of input parameters and output objects** recorded, and the version is written into each job.

---

## 8. From the GUI block to a web form

| SmartAssembly | Web form | Note |
|---------------|----------|---------|
| `CHECKBOX_PARAM` | A checkbox | The "done" flag used to split into several phases corresponds to a **multi-step form (wizard)** |
| `RADIOBUTTON_PARAM` | A group of single-choice buttons (or a choice box) | |
| `USER_INPUT_PARAM` (`REQUIRED`, `TOOLTIP`) | A number or text input, required, with a hint | Add the unit, `MIN`, `MAX`, default |
| `USER_SELECT`, `USER_SELECT_MULTIPLE` (pick edges, faces, features in the model) | **Not supported on the order web.** Replace with: choose by **name or role** from a ready list; or allow it only in the **interactive mode in the add-in** (using the Inventor object picker) | The biggest difference from SmartAssembly |
| `MESSAGE_BOX`, `MESSAGE_BOX_EX` | On the web: a notice or a confirmation box in the form. On the worker: **write to the job log**; a question box takes the declared **default value** and does not wait | A default value must be declared for every question box so a job does not hang |
| `PROCESSING_BOX_START/SET_STATE/END` | **Job progress** (steps shown on the job page like in the video: building the assembly...) | Name the steps for display |
| `IF` in the GUI block (show or hide a field) | Show or hide a field on the web | |
| (added) | `ASSERT <condition> "message"` | A value-checking rule, the message shows on the web and submitting stops if false (replacing the "IF then MESSAGE_BOX then RETURN" style) |
| (added) | `OPTIONS`, `LOOKUP table` | A choice list taken from a catalog table, with relations between choices (valid ratings and voltage classes) |

**When is the FORM block recomputed?** Your table notes that this is only an inference from sample code. Since this is your language, **define it yourself**: the FORM block is **a pure function that runs again whenever any value changes** (server or WebAssembly). Each run returns: the fields shown, the fields hidden, the valid values, the list of `ASSERT` errors, a rough price. This gives predictable behavior that matches the web experience.

---

## 9. Modularization and paths

| SmartAssembly | Suggestion |
|---------------|---------|
| `INCLUDE lib:File_Name.tab` | Keep the statement `INCLUDE lib:File_Name` (done: `Scripts/lib/<name>.gea`, see `gea-language-spec.md` section 20) |
| The `lib:` prefix is resolved through configuration (`SA_Paths.txt`), no hard-coded paths | Resolved by the **library store of the product version** (configured on the server, each worker has a synchronized copy); hard-coded paths are banned |
| A module shares the variables of the parent file (scope not isolated) | **Settled (D6): share variables** like SmartAssembly. A duplicate declaration is error `GEA2003`; a library with its own `PARAM_PREFIX` has it added to the valid prefixes of the parent script |

---

## 10. Error handling and debugging

| SmartAssembly | Suggestion |
|---------------|---------|
| `BEGIN_CATCH_ERROR ... END_CATCH_ERROR`, the `ERROR` flag, `CLEAR_CATCH_ERROR` | Keep the statements. Improvement: **entering a catch block clears the flag automatically**, avoiding false positives; an exception from Inventor (COM) is turned into the `ERROR` flag with its text |
| `FIX_FAIL_UDF` | `FIX_FAIL_FEATURE` (try to repair a failed feature), used later, not needed at Level 1 |
| The pattern "try the left UDF, on error try the right UDF" | Keep, use the catch block |
| The Creo trail file (file path, line number, message) | **Job log**: script file name, **line, column**, statement, message, time; stored per job and shown on the web |
| Highlighting the coverage of lines that ran (VS Code extension, `DEBUG_TAB_PERFORMANCE`) | The interpreter records an **execution trace** (which lines ran); the job page and Product Studio highlight the lines that ran |
| (added) | `PRINT`, log levels (info, warning, error), `STOPWATCH` for each step |

---

## 11. External integration

| SmartAssembly | Suggestion for this system | Note |
|---------------|--------------------------|---------|
| `EXCEL_*` (open, read, write, close, `EXCEL_DISCONNECT`) | Read and write **xlsx files with a library** (not driving the Excel application); the statements `TABLE_LOAD`, `TABLE_GET`, `TABLE_SET` | No Excel process left behind, no need to install Excel on the worker |
| `JSON_LOAD_DOCUMENT`, `JSON_GET_VALUE`, `JSON_SET_VALUE` | Keep | Used for the input of the order and exchange with the API |
| `READ_FEATURE_XML`, `MODIFY_FEATURE_XML` | Not needed | Specific to Creo |
| Drawings: `CREATE_DRW_VIEW_GENERAL`, `CREATE_DRW_NOTE`, `SET_DRW_*`, `EXPORT_DRW_PDF` | `DRW_NEW`, `DRW_ADD_VIEW`, `DRW_ADD_SECTION`, `DRW_ADD_BALLOONS`, `DRW_ADD_PARTSLIST`, `DRW_ADD_NOTE`, `DRW_SET_*`, `EXPORT_PDF`, `EXPORT_STEP`, `EXPORT_IMAGE` | Maps to the parts of an Inventor drawing document and the PDF and STEP export translators. This is the most fragile part (see risks) |
| `FILE_OPEN/READ_LINE/WRITE_LINE/CLOSE`, `DELETE_FILE`, `WAIT_FOR_FILE` | Keep but allow only in the **working folder of the job** | Block writing outside the job folder |
| Running `.exe`, VB, Java, C++, Excel macros, PowerShell through COM | **Banned by default** | A script on the worker has high privileges; enable only for trusted scripts, with an allow list |

**On safety:** because a script runs on a worker machine with high privileges, the principle is that **the person ordering only supplies values, not a script**. Scripts come only from the product author who is logged in and has the right to publish. A published version cannot be edited.

---

## 12. Geometry analysis

| SmartAssembly | Inventor equivalent | Priority |
|---------------|----------------------|---------|
| `GET_SURFACE_NORM` | Gets the face normal from the face evaluator | Low (Level 4 onward) |
| `VECTOR_NORMALIZE`, `VECTOR_FROM_POINTS` | Vector arithmetic | Low |
| `MEASURE_DISTANCE` | Measures the minimum distance between two objects | Low |
| `GET_SURFACE_CONTOUR` | The outline of a face (the edge loops) | Low |
| `GET_REF_VERTEX`, `GET_REF_POS` | The start and end vertex of an edge, the position | Low |

For the first levels (a Part, tank and cover) this group is **hardly needed**, because the dimensions come from parameters, not from analyzing faces and edges. It is needed when placing pipes by face, finding two opposite faces to place a joint. The specific API names are checked when doing the work.

---

## 13. Summary of statement groups and priority

Priority follows the levels of the object (section 5d of `inventor-addin-plan.md`): **P0** needed for Level 1 (tank and cover), **P1** Level 2, **P2** Levels 3-4 (bushings, pipes, assembly), **P3** Level 5 and extensions.

| Group | Statements | Priority |
|------|------|---------|
| Declarations | `DECLARE_VARIABLE`, `DECLARE_REFERENCE`, `DECLARE_ARRAY`, `DECLARE_MAP`, `DECLARE_STRUCT` | P0 (variables), P1 (arrays), P2 (map, struct) |
| Flow | `IF`, `FOR`, `WHILE`, `BREAK`, `CONTINUE`, `RETURN` | P0 |
| Expressions | Arithmetic, comparison, `MIN/MAX/ABS/ROUND`, units | P0 |
| Parameters | `SET_PARAM`, `GET_PARAM`, `SET_PARAM_EXPR` | P0 |
| Building | `CREATE_FEATURE` (builder), `SET_REF_NAME`, `REGEN_MDL` | P0 |
| Labeling | `SET_REF_ATTR` | P0 |
| Searching | `SEARCH_MDL_REF`, `SEARCH_MDL_REFS`, `REF_VALID`, `INVALIDATE_REF` | P1 (by name, attribute), P2 (by face, edge) |
| Errors | `BEGIN_CATCH_ERROR`, `CLEAR_CATCH_ERROR`, `ERROR` | P0 |
| Interface | `USER_INPUT_PARAM`, `CHECKBOX_PARAM`, `RADIOBUTTON_PARAM`, `ASSERT`, `IF` in FORM | P0 |
| Progress | `PROCESSING_BOX_*` (becoming job steps), `PRINT` | P0 |
| Messages | `MESSAGE_BOX*` | P1 |
| Picking in the model | `USER_SELECT*` (interactive mode only) | P3 |
| Assembly | `ADD_COMPONENT`, `ASSEMBLE` (`MATE`, `FLUSH`, `INSERT`, `ANGLE`) | P2 |
| Tables, data | `TABLE_*`, `JSON_*`, `LOOKUP` | P1 (catalog, price), P2 |
| Drawings, file export | `DRW_*`, `EXPORT_PDF`, `EXPORT_STEP`, `EXPORT_IMAGE` | P2 (image, STEP), P2 (drawing) |
| Geometry | `GET_SURFACE_NORM`, `MEASURE_DISTANCE`... | P3 |
| Module | `INCLUDE` | P1 |
| Utilities | `STOPWATCH`, `FILE_*` (inside the job folder) | P2 |

---

## 14. Interpreter architecture

Unlike the earlier plan (create an abstract "build plan" and only then execute), a SmartAssembly-style language is **imperative**: each line runs and may depend on the result of the previous line (for example find a reference and only then keep building). Therefore:

### 14.1. Pipeline
1. A line-based **lexer**: recognizes upper-case keywords, constants, identifiers, records the line and column.
2. A statement-based **parser**: each statement has a fixed parameter shape (a statement table declares the number of parameters, types, optional parameters); nested blocks (`IF/END_IF`, `FOR/END_FOR`, `CREATE_FEATURE/END_CREATE_FEATURE`).
3. **Semantic analysis**: undeclared names, types and units, a missing `END_*`, a statement not allowed in that block (for example a build statement in the FORM block).
4. The **interpreter** runs the statement tree and calls the **Host interface** for everything that touches the model.

### 14.2. The Host interface and the two executors
| Executor | Used where | What it does |
|-------------|-----------|--------|
| **Inventor Host** | Worker (and add-in) | Calls the real Inventor API: build, set Parameters, find references, drawings, export files |
| **Simulation Host (virtual model)** | Server and browser, tests | Keeps a model **in memory**: Parameters, the list of components and positions, labels, flags, BOM, price, estimated mass; records a **statement trace** |

The Simulation Host is the "virtual host model" idea: it allows a **dry run on the web without Inventor**, producing the BOM, price, the list of `ASSERT` errors, a diagram preview (from the statement trace) and automated tests.

**A limit that must be stated clearly**: the simulation cannot recreate real geometry. Statements that depend on faces and edges (`SEARCH_MDL_REFS` by face, distance measurement) only run on the Inventor Host; in the simulation they return a fake value or are skipped and are **marked "cannot be simulated"** in the dry-run result. So the dry run on the web guarantees the **logic, rules, price, BOM**, while the geometric correctness still needs a dry run on a worker.

### 14.3. Consequences for the DSL plan
- The "build plan" step in section 5c is replaced by the **Host interface and the statement trace**. The rest of section 5c (lexer, parser, semantics, units, error messages with line and column, the D0-D5 roadmap) can still be used.
- The preview canvas on the web reads the **statement trace of the Simulation Host**, keeping the idea of "one definition for the form, the checks and the preview".
- The Adapter still only receives an `Inventor.Application` object and is the party that implements the **Inventor Host**.

---

## 15. An illustrative example of the proposed syntax

This example only illustrates the style and the mapping, **statement names are not settled**. The object is Level 1 (tank and cover).

```
! May bien ap - Cap 1: thung va nap
BEGIN_FORM
  USER_INPUT_PARAM TR_TankLength LENGTH 100 mm MIN 40 MAX 500 REQUIRED TOOLTIP "Chieu dai ngoai thung"
  USER_INPUT_PARAM TR_TankWidth  LENGTH 60 mm  MIN 30 MAX 300 REQUIRED
  USER_INPUT_PARAM TR_TankHeight LENGTH 40 mm  MIN 20 MAX 200 REQUIRED
  USER_INPUT_PARAM TR_Wall       LENGTH 2 mm   MIN 1 MAX 10 REQUIRED
  CHECKBOX_PARAM   TR_HasManhole TRUE
  IF TR_HasManhole
    USER_INPUT_PARAM TR_ManholeDiameter LENGTH 40 mm MIN 20 MAX 100
  END_IF
  RADIOBUTTON_PARAM TR_Paint "Grey" "Green"
  ASSERT TR_Wall < MIN(TR_TankLength, TR_TankWidth) / 2 "Do day thanh phai nho hon mot nua canh ngan"
END_FORM

BEGIN_BUILD
  DECLARE_VARIABLE LENGTH innerR
  DECLARE_REFERENCE tank
  DECLARE_REFERENCE cover

  PROCESSING_BOX_SET_STATE "Dung thung"
  CREATE_FEATURE builder:Tank tank
    FEATURE_IN "Length" TR_TankLength
    FEATURE_IN "Width"  TR_TankWidth
    FEATURE_IN "Height" TR_TankHeight
    FEATURE_IN "Wall"   TR_Wall
    FEATURE_OUT tank "Body"
  END_CREATE_FEATURE
  SET_REF_NAME tank "TR_Tank*"
  SET_REF_ATTR tank "ROLE" "TANK"

  IF TR_HasManhole
    CREATE_FEATURE builder:Manhole tank
      FEATURE_IN "Diameter" TR_ManholeDiameter
    END_CREATE_FEATURE
  END_IF

  PROCESSING_BOX_SET_STATE "Dung nap"
  BEGIN_CATCH_ERROR
    CREATE_FEATURE builder:Cover cover
      FEATURE_IN "Tank" tank
      FEATURE_OUT cover "Body"
    END_CREATE_FEATURE
  END_CATCH_ERROR
  IF ERROR
    PRINT "Khong dung duoc nap"
    RETURN
  END_IF
  SET_REF_ATTR cover "ROLE" "COVER"

  REGEN_MDL
  SET_APPEARANCE tank TR_Paint
END_BUILD
```

### Line-by-line mapping table

| The line the user types | What the interpreter understands | What the Inventor Host does | What the Simulation Host does |
|--------------------|-----------------------|--------------------|--------------------|
| `USER_INPUT_PARAM ... LENGTH 100 mm MIN 40 MAX 500` | Declares a parameter with a unit and limits | Creates a User Parameter with a name, unit mm | Adds it to the virtual model, checks the value range |
| `IF TR_HasManhole` in FORM | A field-visibility condition | (does not run in Inventor) | Recomputed when a value changes, shows or hides the field on the web |
| `ASSERT ... "message"` | A validation rule | Stops the job if false | Returns the error list to the form |
| `DECLARE_REFERENCE tank` | Declares a reference | A slot holding an Inventor object | A slot holding a virtual reference |
| `CREATE_FEATURE builder:Tank ... END_CREATE_FEATURE` | Calls the build function with the arguments | Runs the C# function that builds the body with the Inventor API in a Transaction | Records a trace "create tank with these dimensions" and adds a virtual block |
| `FEATURE_OUT tank "Body"` | Takes the output object by name | Assigns the body just created to the variable | Assigns the virtual block |
| `SET_REF_NAME tank "TR_Tank*"` | Sets a unique name | Renames the body, adds a suffix automatically on a clash | Sets a virtual name |
| `SET_REF_ATTR tank "ROLE" "TANK"` | Attaches a label | Attaches an AttributeSet to the body | Records the label |
| `IF TR_HasManhole ... CREATE_FEATURE builder:Manhole` | A conditional build | Runs the builder when true | Records the trace when true |
| `BEGIN_CATCH_ERROR ... END_CATCH_ERROR` | Catches errors | A COM exception sets the `ERROR` flag | An error flag if the virtual builder reports an error |
| `PROCESSING_BOX_SET_STATE "..."` | Updates the progress | Sends the step to the server for the job page | Writes to the trace |
| `REGEN_MDL` | Updates the model | Updates the Inventor document | Does nothing (may recompute the estimated mass) |
| `SET_APPEARANCE tank TR_Paint` | Sets the paint color | Assigns an Appearance to the body | Writes to the trace to draw the preview |

---

## 16. Decisions to settle (updating D1-D8 in section 5c)

| # | Decision | Suggestion |
|---|-----------|---------|
| D1 | Style | **Direction chosen:** line-by-line imperative, upper-case keywords, three blocks, in the SmartAssembly style (replacing the hybrid option of the earlier version) |
| D2 | External syntax | A custom text per sections 3-5; **the `.gea` file extension** (settled), details in `gea-language-spec.md` |
| D3 | Parser | Hand-written line by line, a statement table declares the parameter shape of each statement |
| D4 | Units | Has `LENGTH`, `ANGLE` with static checking |
| D5 | Reference names | Use the Parameter names directly; attribute labels for entities |
| D6 | Variable scope of a module | **Settled: shared variables like SmartAssembly** (section 9, `gea-language-spec.md` section 20) |
| D7 | Language version | A version declaration line at the top of the file |
| D8 | Error messages | Line, column, statement, excerpt; in English |
| **D9 (new)** | When FORM is recomputed | Run again whenever any value changes (section 8) |
| **D10 (new)** | Self-recovering references | Do after Level 2 (section 6) |
| **D11 (new)** | The source of components at Level 1 | C# build functions first, iFeatures and iParts from Level 3 (section 7) |
| **D12 (new)** | Running external programs | Banned by default (section 11) |

---

## 17. Risks

| Risk | How to reduce it |
|--------|-----------|
| The language grows large and is unusable for lack of a component library | Build the library in parallel and level by level; Level 1 needs only a few builders |
| The Simulation Host must keep up with every new statement | Each statement added must state its behavior on both Hosts, or be marked "cannot be simulated" |
| A dry run on the web gives a result different from Inventor | Promise only the logic, rules, price, BOM; have an output comparison suite between the two Hosts on the same sample value sets |
| References become invalid after a regenerate | Attach attribute labels right at creation; build self-recovering references (D10) |
| A script runs dangerous statements on a worker | The person ordering does not write scripts; controlled publishing; by default ban running external programs and writing files outside the job folder |
| A dialog waiting for a person in the worker | Every question box has a default value; run Inventor in a no-dialog mode |
| An infinite loop | A limit on the iteration count and the maximum time per job |
| Copying a proprietary language too closely | Name things and write documentation ourselves; keep only the structural ideas; review the terms if commercialized |
| Inventor API names change in 2027 | Check each API part when implementing, note it in the statement table |

---

## 18. Next things to do right away

1. (D6 is settled: shared variables) Settle the file extension; confirm D1 as imperative in the SmartAssembly style.
2. **D0 (by hand):** turn the example in section 15 into a real file for Level 1, to serve as a yardstick for the syntax.
3. Write the **P0 statement table** (the number of parameters, types, the behavior on the Inventor Host and the Simulation Host).
4. Build the lexer, parser and interpreter for P0 together with the **Simulation Host** (no Inventor needed), with tests and the statement trace.
5. Write the **Inventor Host** for P0 and the C# build functions for the tank and cover (reusing the Adapter part of the add-in).
6. Wire into the web: the FORM block generates the form, a dry run with the Simulation Host, sending the job to a worker.
