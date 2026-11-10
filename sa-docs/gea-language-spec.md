# `.gea` language specification (version 1, draft)

A `.gea` file is the equivalent of a `.tab` file (SmartAssembly) for your Inventor and web system. This document fixes the **syntax, types, units, every statement and the behavior** so that a lexer, parser and interpreter can be written.

Companion: `tank-level1.gea` (the sample file for Level 1, exactly following this specification).
Related: `smartassembly-to-inventor-mapping.md` (the mapping from `.tab`), `inventor-addin-plan.md`, `cadflow-order-to-model-plan.md`.

> If the syntax example in section 15 of `smartassembly-to-inventor-mapping.md` differs from this document, **this document is the standard**.

---

## 1. Position and scope

- `.gea` describes **one product**: the input form, the lookup tables, the logic that builds the Inventor model.
- It keeps the `.tab` style: imperative, **one statement per line**, upper-case keywords, three blocks (form, tables, build), no parenthesized function-call syntax for statements. Expressions still have parentheses and helper functions.
- Where it runs:

| Block | Runs on | When |
|------|--------|---------|
| `BEGIN_FORM` | Server (and add-in) | Whenever a value on the form changes, and when the order is submitted |
| `BEGIN_TABLES` | Server and worker | Loaded once |
| `BEGIN_BUILD` | Worker (and add-in) | When a job runs |

- **Scope of v1:** every **P0** statement (enough for Level 1: tank, cover, skirt) is fully specified. The **P1, P2, P3** statements only reserve a place in section 11 so their names are not taken later.
- Differences from `.tab`, mainly: UTF-8 encoding; units are part of the type; the FORM block generates the web form and is a pure function; error messages have a code, line and column; references are attached by attributes; there are no interactive dialogs in BUILD.

---

## 2. Lexical conventions

| Item | Rule |
|----------|---------|
| Encoding | UTF-8 (a BOM is allowed). Non-ASCII text is allowed in strings and comments |
| Lines | One statement per line. A `\` at the end of a line joins it with the next line |
| Indentation | Use Tab or spaces for readability, it **has no syntactic meaning** |
| Comments | `!` to the end of the line; `/* ... */` over several lines. A `!` inside a string is not a comment |
| Keywords | **Upper case**, case-sensitive (`IF` is right, `if` is wrong) |
| Identifiers | `[A-Za-z_][A-Za-z0-9_]*`, case-sensitive. **Two names that differ only in letter case are not allowed** (Inventor may not tell them apart) |
| Integer | `12`, `-3` |
| Real | `3.5`, `0.3` (decimal point) |
| String | `"..."`, with escapes `\"`, `\\`, `\n`, `\t` |
| Logic | `TRUE`, `FALSE` |
| Quantity with a unit | Number + unit: `100 mm`, `45 deg` (writing it joined, `100mm`, is allowed) |
| Length units | `mm`, `cm`, `m`, `in` |
| Angle units | `deg`, `rad` |
| Operators | `+ - * / %`, `== <> < <= > >=`, `AND OR NOT`, parentheses `( )` |
| Increment/decrement | `i++`, `i--` (as a statement of their own) |
| Assignment | `name = expression` (a statement of its own) |

Reserved words: every keyword in this document, plus `TRUE`, `FALSE`, `ERROR`, `ERROR_MESSAGE`.

---

## 3. File structure

```
GEA_VERSION 1                  (required, the first statement)
PRODUCT_NAME "..."             (required)
PARAM_PREFIX XX_               (recommended)
TARGET PART                    (required: PART or ASSEMBLY)

BEGIN_FORM ... END_FORM        (optional)
BEGIN_TABLES ... END_TABLES    (optional)
BEGIN_BUILD ... END_BUILD      (required)
```

- The block order is fixed: FORM, TABLES, BUILD.
- `PARAM_PREFIX`: every parameter declared in FORM must start with this prefix (checked in the semantic step). Several prefixes can be listed (`PARAM_PREFIX GT_ TR_`); see section 20 about `INCLUDE`. Purpose: avoid name clashes with the user's own Parameters in Inventor.
- `TARGET`: the kind of document that BUILD creates or uses. Which document to open (the open one or a new one from a template) is decided by the **Host**, not by the script.
- A variable declared in a block **is only valid in that block**. Only **parameters** (declared with a `*_PARAM` statement) are passed from FORM to BUILD.

### 3.1. Which statements may be used in which block

| Statement group | Header | FORM | TABLES | BUILD |
|-----------|--------|------|--------|-------|
| `GEA_VERSION`, `PRODUCT_NAME`, `PARAM_PREFIX`, `TARGET` | ✓ | | | |
| `DECLARE_*`, assignment, expressions | | ✓ | | ✓ |
| `IF`, `FOR`, `WHILE`, `BREAK`, `CONTINUE`, `RETURN` | | ✓ (no `WHILE`) | | ✓ |
| `BEGIN_GROUP`, `USER_INPUT_PARAM`, `CHECKBOX_PARAM`, `RADIOBUTTON_PARAM` | | ✓ | | |
| `ASSERT`, `PRINT` | | ✓ | | ✓ |
| `BEGIN_TABLE`, `COLUMN`, `ROW` | | | ✓ | |
| `LOOKUP` (P1) | | ✓ | | ✓ |
| `SET_PARAM`, `GET_PARAM`, `SET_PARAM_EXPR` | | | | ✓ |
| `CREATE_FEATURE`, `SET_REF_NAME`, `SET_REF_ATTR`, `GET_REF_*` | | | | ✓ |
| `SET_APPEARANCE`, `REGEN_MDL` | | | | ✓ |
| `PROCESSING_BOX_*` | | | | ✓ |
| `BEGIN_CATCH_ERROR`, `CLEAR_CATCH_ERROR`, `FAIL` | | | | ✓ |

A statement used in the wrong block is a **semantic error** (code `GEA2020`), for example `CREATE_FEATURE` in FORM.

---

## 4. Types and units

### 4.1. The types

| Type | Description | Example |
|------|-------|-------|
| `INTEGER` | A whole number | `3` |
| `DOUBLE` | A real number, no unit | `0.5` |
| `BOOL` | Logic | `TRUE` |
| `STRING` | A string | `"Grey"` |
| `LENGTH` | A length, stored internally in mm | `100 mm` |
| `ANGLE` | An angle, stored internally in degrees | `30 deg` |
| `REFERENCE` | A reference to an object in the model (body, feature, ...) | |
| `ARRAY` | A list of elements of the same type | P1 |
| `MAP` | A dictionary with string keys | P1 |
| `POINT`, `VECTOR`, `STOPWATCH` | | P2 |

The Host is responsible for converting units when it calls Inventor (the internal unit of the API is cm and radians). The script **never** sees cm or radians.

### 4.2. Operator rules

| Operation | Result | Note |
|------|---------|---------|
| `LENGTH + LENGTH`, `LENGTH - LENGTH` | `LENGTH` | Units are converted automatically (`1 cm + 5 mm` is right) |
| `LENGTH * DOUBLE/INTEGER`, `DOUBLE * LENGTH`, `LENGTH / DOUBLE/INTEGER` | `LENGTH` | |
| `LENGTH / LENGTH` | `DOUBLE` | A ratio |
| `LENGTH * LENGTH` | **Error** `GEA2002` | v1 has no area |
| `ANGLE` | Like `LENGTH` | |
| Comparing `LENGTH` with `LENGTH` | `BOOL` | Comparing `LENGTH` with a bare number: **error**, unless the number is `0` |
| `INTEGER` and `DOUBLE` | `INTEGER` is promoted to `DOUBLE` automatically | |
| Assigning `DOUBLE` to `LENGTH` | **Error** | You must multiply by `1 mm` |
| `STRING + STRING` | `STRING` | String concatenation. `STR(x)` converts a number to a string |
| `%` | `INTEGER` only | |

Division by 0 is the run-time error `GEA5002`.

---

## 5. Declarations and variables

| Statement | Syntax | Note |
|------|---------|---------|
| Variable | `DECLARE_VARIABLE <type> <name> [value]` | Types: `INTEGER`, `DOUBLE`, `BOOL`, `STRING`, `LENGTH`, `ANGLE`. Without a value it takes the default of the type (0, `FALSE`, an empty string) |
| Reference | `DECLARE_REFERENCE <name>` | The initial value is "unassigned" |
| Array (P1) | `DECLARE_ARRAY <name> [<type>]` | The default type is `REFERENCE` |
| Map (P1) | `DECLARE_MAP <type> <name>` | The key is always `STRING` |
| Assignment | `<name> = <expression>` | The type must be valid per section 4.2. A parameter from FORM is **read-only** in BUILD |
| Increment, decrement | `<name>++`, `<name>--` | `INTEGER` only |

Rules:
- Declarations should go at the top of a block, grouped by type (a recommendation, not required).
- Using an undeclared name: `GEA2001`. Declaring a name twice: `GEA2003`.
- The variables `ERROR` (`BOOL`) and `ERROR_MESSAGE` (`STRING`) are built in, read-only (see section 9.6).

---

## 6. Control flow

| Construct | Syntax | Note |
|----------|---------|---------|
| Condition | `IF <expr>` / `ELSE_IF <expr>` / `ELSE` / `END_IF` | `<expr>` must be `BOOL` |
| Counting loop | `FOR <i> FROM <a> TO <b> [STEP <s>]` ... `END_FOR` | `a`, `b`, `s` are `INTEGER`. **`TO` includes the last value.** `STEP` defaults to 1, may be negative, not 0 |
| Array iteration (P1) | `FOR <x> REF <array>` ... `END_FOR` | See the no-delete rule below |
| Map iteration (P1) | `FOR <x> REF <map>` ... `END_FOR` | Use `x.key`, `x.value` |
| Conditional loop | `WHILE <expr>` ... `END_WHILE` | BUILD only. Iteration limit, see below |
| Leave a loop | `BREAK`, `CONTINUE` | |
| Finish early | `RETURN` | In BUILD: ends BUILD successfully. In FORM: ends the FORM evaluation |

- The **loop variable** `i` of `FOR ... FROM` is created implicitly, of type `INTEGER`, and exists only inside the loop.
- **Iteration limit**: each `WHILE` and `FOR` runs at most **10000 iterations** (configurable). Beyond that: `GEA5001`, the job fails.
- **Do not delete array elements while iterating over it**: the interpreter iterates over a **copy** so there is no error; if you think you need to delete, use `FOR ... FROM ... STEP -1` by index.
- Array indexes **start at 0**.

---

## 7. The FORM block

The FORM block describes the **web form** and the validation rules. It is a **pure function**: the same input values always give the same result, and it does not touch Inventor.

### 7.1. Statements

| Statement | Syntax | Meaning |
|------|---------|---------|
| Group | `BEGIN_GROUP "<title>"` ... `END_GROUP` | Groups fields into a region on the form (like the Rating, Cooling, Options frames in the video) |
| Input field | `USER_INPUT_PARAM <name> <type> <default> [<unit>] [MIN <v>] [MAX <v>] [STEP <v>] [REQUIRED] [LABEL "<text>"] [TOOLTIP "<text>"]` | An input parameter. Types: `LENGTH`, `ANGLE`, `INTEGER`, `DOUBLE`, `STRING` |
| Checkbox | `CHECKBOX_PARAM <name> <TRUE\|FALSE> [LABEL "<text>"] [TOOLTIP "<text>"]` | A `BOOL` parameter |
| Single choice | `RADIOBUTTON_PARAM <name> "<a>" "<b>" ... [DEFAULT "<x>"] [LABEL "<text>"]` | A `STRING` parameter. The default is the first choice, unless `DEFAULT` is given |
| Rule | `ASSERT <expr> "<message>" [WARN]` | When false, reports an error (or a warning with `WARN`) |
| Message text | `PRINT <expression>` | In FORM, shown as a note on the form when this line is reached |

For `USER_INPUT_PARAM`:
- `<default>` is a number (it may carry a unit for `LENGTH` and `ANGLE`). For example `LENGTH 100 mm`.
- `MIN`, `MAX`, `STEP` are numbers; **without a unit, the unit of the parameter is used**. For example `MIN 40 MAX 500` for `LENGTH 100 mm` means 40 mm to 500 mm.
- `REQUIRED`: the value must not be empty. For a number with a default it is always satisfied.
- `LABEL` defaults to the parameter name.
- A violation of `MIN`/`MAX` generates an error such as: *"TR_Wall must be within [1 mm, 10 mm]"*.

`DECLARE_VARIABLE`, assignment, `IF/ELSE_IF/ELSE/END_IF`, `FOR`, `RETURN` and `LOOKUP` (P1) can also be used in FORM.

### 7.2. Showing or hiding a field

Put a `*_PARAM` statement inside `IF ... END_IF`. When the condition is false, the field is **hidden**. Rules:
- A hidden field takes its **default value** and can still be read in BUILD.
- A hidden field is **not** checked for `MIN`, `MAX`, `REQUIRED`.
- The parameter still exists as if it were declared; it is only not shown for input.

### 7.3. How the FORM block is evaluated

1. Input: the map `name -> value` that the user is currently entering. A missing field uses its default.
2. Run the block **from top to bottom**. Using a parameter before it is declared is an error.
3. Output:

| Field | Meaning |
|--------|---------|
| Field list | For each parameter: shown or hidden, the final value (after applying the default to a hidden field), valid or not |
| Errors | The failing `ASSERT`s, violations of `MIN`/`MAX`/`REQUIRED`, with messages |
| Warnings | The failing `ASSERT ... WARN`s |
| Notes | The `PRINT`s that ran |

4. **An order can be submitted only when no errors remain.** The server runs the FORM block again when it receives the order and does not trust the result from the browser.
5. The value before a choice was hidden is lost (reset to the default). If you want it remembered when the field shows again, that is the job of the UI, not the language.

---

## 8. The TABLES block (P1)

Describes lookup tables (a catalog of dimensions), not a price table.

```
BEGIN_TABLES
  BEGIN_TABLE Rating
    COLUMN kVA INTEGER
    COLUMN Length LENGTH mm
    ROW 630  1800
    ROW 1000 2100
  END_TABLE
END_TABLES
```

| Statement | Syntax | Meaning |
|------|---------|---------|
| `BEGIN_TABLE` ... `END_TABLE` | `BEGIN_TABLE <name>` | One table |
| `COLUMN` | `COLUMN <name> <type> [<unit>]` | A column, in order |
| `ROW` | `ROW <value> ...` | A data row, the number of values equals the number of columns |
| `SOURCE` | `SOURCE "<path>"` | Instead of `ROW`: loads from CSV or XLSX in the product folder |
| `LOOKUP` | `LOOKUP <table> <key column> <key> <value column> <variable>` | Assigns the variable the value of column `<value column>` of the row where `<key column> == <key>`. If no row matches: `GEA4030` |

---

## 9. The BUILD block

### 9.1. Execution model

1. The Host prepares the document according to `TARGET` (in the add-in: the open document; in the worker: a new document from a template).
2. The Host **synchronizes every FORM parameter as a User Parameter** of the same name and the right unit, before the first line of BUILD. If a Parameter with the same name already exists that was **not created by this `.gea`** (no ownership mark): `GEA4010`, stop.
3. The Host opens **one Transaction** around the whole BUILD.
4. Run line by line. An error that is not caught, or `FAIL`, **cancels the Transaction** (the model returns to how it was) and the job fails.
5. Reaching `END_BUILD` or a `RETURN` without an error: **commit the Transaction**.

BUILD **has no dialogs**. No statement waits for the user.

### 9.2. Parameters

| Statement | Syntax | Meaning |
|------|---------|---------|
| `SET_PARAM` | `SET_PARAM <name> <expression>` | Sets the value of a User Parameter, creating it if missing. The type and unit follow the expression |
| `SET_PARAM_EXPR` | `SET_PARAM_EXPR <name> "<expression string>"` | Sets an **Inventor expression** (for example `"TR_TankLength / 2"`), so the Parameter depends on other Parameters in the model |
| `GET_PARAM` | `GET_PARAM <name> <variable>` | Reads the value of a Parameter into a variable, with the right type and unit |

A Parameter created with `SET_PARAM` also gets the ownership mark of this `.gea` file.

### 9.3. Building with a builder: `CREATE_FEATURE`

```
CREATE_FEATURE <source> [ON <reference>]
    FEATURE_IN "<input name>" <expression or reference>
  ...
    FEATURE_OUT <variable> "<output name>"
  ...
END_CREATE_FEATURE
```

| Part | Meaning |
|------|---------|
| `<source>` | `builder:<Name>`: a build function written in C#. (P1) `lib:<path>`: an iFeature, iPart or a sample assembly in the product library |
| `ON <reference>` | Optional. The target object the builder acts on (for example the tank to cut a hole in) |
| `FEATURE_IN` | Passes an input **by name**. The type and unit must match the builder **manifest** |
| `FEATURE_OUT` | Assigns an output object (by name) to a `REFERENCE` variable that was declared |

Check rules (in the semantic step, once the manifest exists):
- An input that is not in the manifest: `GEA2010`.
- A required input is missing: `GEA2011`.
- An output that is not in the manifest: `GEA2012`.
- Wrong type or unit: `GEA2002`.
- The source does not exist: `GEA2013`.

By **name**, not by index, so a library that changes the order does not break scripts.

### 9.4. References

| Statement | Syntax | Meaning | Priority |
|------|---------|---------|---------|
| `SET_REF_NAME` | `SET_REF_NAME <ref> "<name>"` | Renames the object. A name ending in `*` makes the Host add a suffix to keep it unique (`TR_Tank_1`...). A duplicate name without `*`: `GEA4011` | P0 |
| `GET_REF_NAME` | `GET_REF_NAME <ref> <variable>` | Reads the name | P0 |
| `SET_REF_ATTR` | `SET_REF_ATTR <ref> "<key>" <expression>` | Attaches an attribute (stored in the file, still there after reopening), used to find it again | P0 |
| `GET_REF_ATTR` | `GET_REF_ATTR <ref> "<key>" <variable>` | Reads the attribute | P0 |
| `REF_VALID` | `REF_VALID <ref> <BOOL variable>` | Whether it is still valid | P1 |
| `SEARCH_MDL_REF`, `SEARCH_MDL_REFS` | `SEARCH_MDL_REF <variable> TYPE <kind> [NAME "<pattern>"] [ATTR "<key>" <value>]` | Finds one or several objects. Kinds: `BODY`, `FEATURE`, `SKETCH`, `WORKPLANE`, `OCCURRENCE`, `PARAMETER` (P2: `FACE`, `EDGE`). The name pattern uses `*` and `?` | P1 |
| `INVALIDATE_REF`, `COPY_REF`, `REF_EQUAL` | | | P1 |

Using an unassigned reference: `GEA4012`. Standard attribute convention: the key `ROLE` for the role (`TANK`, `COVER`, ...).

### 9.5. Appearance and update

| Statement | Syntax | Meaning |
|------|---------|---------|
| `SET_APPEARANCE` | `SET_APPEARANCE <ref> <string expression>` | Assigns an appearance (color, display material) by **name** from the configured appearance library. If missing: `GEA4020` |
| `REGEN_MDL` | `REGEN_MDL` | Updates the model. A failure sets `ERROR` when inside a catch block, otherwise the job fails |

### 9.6. Progress and log

| Statement | Syntax | Meaning |
|------|---------|---------|
| `PROCESSING_BOX_START` | `PROCESSING_BOX_START "<title>"` | Starts a group of steps (optional) |
| `PROCESSING_BOX_SET_STATE` | `PROCESSING_BOX_SET_STATE "<text>" [<percent>]` | Updates the current step. In the worker it shows right away on the job page (like the "building the assembly" line in the video) |
| `PROCESSING_BOX_END` | `PROCESSING_BOX_END` | Ends the group |
| `PRINT` | `PRINT <expression>` | Writes to the **job log**, with the line number |

### 9.7. Error handling

| Statement | Syntax | Meaning |
|------|---------|---------|
| `BEGIN_CATCH_ERROR` ... `END_CATCH_ERROR` | | Catches the Host run-time errors inside the block |
| `ERROR` | `BOOL`, read-only | `TRUE` if the block just run had an error |
| `ERROR_MESSAGE` | `STRING`, read-only | The error text |
| `CLEAR_CATCH_ERROR` | | Clears `ERROR` and `ERROR_MESSAGE` |
| `FAIL` | `FAIL <string expression>` | Deliberately fails the job with a message. Inside a catch block it only sets `ERROR` |

Rules (an improvement over `.tab`):
- **Entering `BEGIN_CATCH_ERROR` resets `ERROR` to `FALSE`** on its own, so you do not need to remember to call `CLEAR_CATCH_ERROR` to avoid false positives.
- When there is an error inside the block, **the rest of the block is skipped**, execution continues after `END_CATCH_ERROR`, and `ERROR` is `TRUE`.
- An error from a failing `ASSERT` in BUILD is not caught: the job fails.
- Syntax and semantic errors are never caught (they are detected before running).
- **Needs verification:** when an error happens inside a catch block, whether the **half-done changes of that block itself** are rolled back depends on whether Inventor supports nested transactions as expected. Until verified, the safe assumption: after a failed catch block, **do not rely on the model state** of that block, or use `FAIL`.

---

## 10. Built-in functions in expressions

| Function | Meaning |
|-----|---------|
| `MIN(a, b)`, `MAX(a, b)` | Smallest, largest. The two arguments have the same dimension (the same type or the same unit) |
| `ABS(a)` | Absolute value |
| `ROUND(a, n)`, `FLOOR(a)`, `CEIL(a)` | Rounding. `n` is the number of decimal places. For `LENGTH` the unit is kept |
| `SQRT(a)` | Square root, `DOUBLE` only |
| `STR(a)` | Converts a number or quantity to a string (with its unit) |
| `NUM(s)` | Converts a string to `DOUBLE`. Error `GEA5003` if it is not a number |
| `FIND(s, t)` | Position of the substring `t` in `s`, `-1` if absent |

---

## 11. Reserved statements (not specified in v1)

The following names are **reserved** so that adding them later does not clash with names.

| Priority | Statement |
|---------|------|
| P1 | `CHOICE_PARAM`, `MESSAGE_BOX`, `MESSAGE_BOX_EX`, `SET_OUTPUT`, `JSON_LOAD_DOCUMENT`, `JSON_GET_VALUE`, `JSON_SET_VALUE`, `TABLE_*`, `ADD_ARRAY_ELEM`, `GET_ARRAY_ELEM`, `GET_ARRAY_SIZE`, `CLEAR_ARRAY`, `SET_MAP_VALUE`, `GET_MAP_VALUE`, `HAS_MAP_KEY` |
| P2 | `ADD_COMPONENT`, `BEGIN_ASSEMBLE`/`END_ASSEMBLE` with `MATE`, `FLUSH`, `INSERT`, `ANGLE_CONSTRAINT`; `EXPORT_STEP`, `EXPORT_IMAGE`, `EXPORT_PDF`; `DRW_NEW`, `DRW_ADD_VIEW`, `DRW_ADD_SECTION`, `DRW_ADD_BALLOONS`, `DRW_ADD_PARTSLIST`, `DRW_ADD_NOTE`; `DECLARE_STRUCT`, `START_STOPWATCH`, `GET_STOPWATCH_TIME`; `FILE_OPEN`, `FILE_READ_LINE`, `FILE_WRITE_LINE`, `FILE_CLOSE` (only inside the job folder) |
| P3 | `USER_SELECT`, `USER_SELECT_MULTIPLE` (add-in only), `GET_SURFACE_NORM`, `MEASURE_DISTANCE`, `GET_SURFACE_CONTOUR`, `GET_REF_VERTEX`, `GET_REF_POS`, `VECTOR_NORMALIZE`, `VECTOR_FROM_POINTS`, `FIX_FAIL_FEATURE` |

Statements that **run external programs** (such as `.exe`, PowerShell) are **not in** the v1 language and are banned by default.

---

## 12. The builder manifest

Each builder (a C# function that builds geometry) registers a **manifest** so the semantic step can check `CREATE_FEATURE` without Inventor. It is the contract between the script and the library.

The conceptual content of a manifest:

| Field | Meaning |
|--------|---------|
| `name`, `version` | The name and version of the builder |
| `target` | `PART` or `ASSEMBLY` that the builder acts on |
| `inputs` | The input list: name, type (including `LENGTH`, `REFERENCE`), required or not, default |
| `outputs` | The output list: name, type (usually `REFERENCE`) |
| `simulate` | How the **simulation Host** represents the builder: a bounding block (size), position, an estimated mass by a simple formula. Used for preview and dry runs |

Example manifest content of the builder `Tank` (as JSON, illustrative only):

```json
{
  "name": "Tank",
  "version": "1.0",
  "target": "PART",
  "inputs": [
    { "name": "Length", "type": "LENGTH", "required": true },
    { "name": "Width", "type": "LENGTH", "required": true },
    { "name": "Height", "type": "LENGTH", "required": true },
    { "name": "Wall", "type": "LENGTH", "required": true },
    { "name": "Bottom", "type": "LENGTH", "required": true },
    { "name": "CornerRadius", "type": "LENGTH", "required": false, "default": "0 mm" },
    { "name": "InnerRadius", "type": "LENGTH", "required": false, "default": "0 mm" },
    { "name": "SkirtHeight", "type": "LENGTH", "required": false, "default": "0 mm" }
  ],
  "outputs": [ { "name": "Body", "type": "REFERENCE" } ],
  "simulate": { "boundingBox": ["Length", "Width", "Height + SkirtHeight"] }
}
```

The job records **the version of each builder** that was used.

---

## 13. Error diagnostics

Format: `<file>(<line>,<column>): <level> GEAnnnn: <message>`. Levels: `error`, `warning`.

| Code range | Kind | Examples |
|--------|------|-------|
| `GEA1xxx` | Syntax | `GEA1001` unknown statement; `GEA1002` missing `END_*`; `GEA1003` wrong number of arguments; `GEA1004` unclosed string; `GEA1010-1015` `INCLUDE` errors (section 20) |
| `GEA2xxx` | Semantics | `GEA2001` undeclared name; `GEA2002` wrong type or unit; `GEA2003` duplicate declaration; `GEA2010-2013` `CREATE_FEATURE` errors; `GEA2020` statement used in the wrong block; `GEA2030` missing parameter prefix |
| `GEA3xxx` | Value checks | `GEA3001` `ASSERT` false; `GEA3002` `MIN`/`MAX` violation; `GEA3003` missing `REQUIRED` value |
| `GEA4xxx` | Execution on the Host | `GEA4010` a duplicate Parameter that does not belong to `.gea`; `GEA4011` duplicate object name; `GEA4012` unassigned reference; `GEA4020` appearance not found; `GEA4030` no row in the table; `GEA4099` an unclassified Inventor error (with the original text) |
| `GEA5xxx` | Limits | `GEA5001` loop count exceeded; `GEA5002` division by 0; `GEA5003` string-to-number conversion error; `GEA5004` timeout |

`GEA1xxx` and `GEA2xxx` errors are detected **before running** and listed all at once (not stopping at the first error when possible). `GEA3xxx` errors show on the form. `GEA4xxx` and `GEA5xxx` errors are written to the job log with the line, column and statement.

The interpreter records an **execution trace** (which lines ran) to highlight on the job page and in the editor.

---

## 14. Behavior on the two Hosts (P0 statements)

| Statement | Inventor Host | Simulation Host |
|------|----------------|----------------|
| `*_PARAM` (FORM) | Does not run in Inventor | Runs normally (FORM is a pure function) |
| Parameter synchronization at the start of BUILD | Creates or updates a User Parameter with the right unit, attaches the ownership mark | Writes into the virtual model |
| `SET_PARAM`, `SET_PARAM_EXPR`, `GET_PARAM` | Reads and writes User Parameters | Reads and writes the virtual model (an expression string is only stored, not recomputed, except simple expressions) |
| `CREATE_FEATURE builder:X` | Runs the C# builder that calls the Inventor API inside a Transaction | Uses the manifest `simulate` to add a virtual block; records a trace |
| `SET_REF_NAME`, `GET_REF_NAME` | Renames, reads the name of an Inventor object | Renames a virtual object |
| `SET_REF_ATTR`, `GET_REF_ATTR` | Attaches, reads an AttributeSet | Stores a virtual attribute |
| `SET_APPEARANCE` | Assigns an Appearance by name | Writes to the trace (to draw the color in the preview) |
| `REGEN_MDL` | Updates the document | Does nothing |
| `PROCESSING_BOX_*` | Sends the step to the server for the job page | Writes to the trace |
| `PRINT` | The job log | Writes to the trace |
| `BEGIN_CATCH_ERROR` | Catches exceptions from Inventor | Catches errors from simulate (if any) |
| `FAIL`, `ASSERT` | Cancels the Transaction, the job fails | Reports an error in the dry-run result |

A statement or fragment that depends on real geometry (P1 onward, such as finding faces and edges) is **marked "cannot be simulated"** in the dry-run result of the simulation Host.

---

## 15. Condensed grammar (for the parser author)

```
file        := header form? tables? build
header      := GEA_VERSION int NL
               PRODUCT_NAME string NL
               (PARAM_PREFIX ident NL)?
               TARGET (PART | ASSEMBLY) NL
form        := BEGIN_FORM NL stmt* END_FORM NL
tables      := BEGIN_TABLES NL table* END_TABLES NL
build       := BEGIN_BUILD NL stmt* END_BUILD NL

stmt        := command NL | assign NL | incdec NL | block
command     := KEYWORD arg*                 (the shape of arg follows the statement table)
assign      := ident '=' expr
incdec      := ident ('++' | '--')
block       := if | for | while | group | catch | create_feature
if          := IF expr NL stmt* (ELSE_IF expr NL stmt*)* (ELSE NL stmt*)? END_IF NL
for         := FOR ident FROM expr TO expr (STEP expr)? NL stmt* END_FOR NL
             | FOR ident REF ident NL stmt* END_FOR NL
while       := WHILE expr NL stmt* END_WHILE NL
group       := BEGIN_GROUP string NL stmt* END_GROUP NL
catch       := BEGIN_CATCH_ERROR NL stmt* END_CATCH_ERROR NL
create_feature := CREATE_FEATURE source (ON ident)? NL
                  (FEATURE_IN string expr NL | FEATURE_OUT ident string NL)*
                  END_CREATE_FEATURE NL

expr        := or
or          := and (OR and)*
and         := not (AND not)*
not         := NOT not | cmp
cmp         := add ((== | <> | < | <= | > | >=) add)?
add         := mul ((+ | -) mul)*
mul         := unary ((* | / | %) unary)*
unary       := '-' unary | primary
primary     := number unit? | string | TRUE | FALSE | ident | ident '.' ident
             | FUNC '(' (expr (',' expr)*)? ')' | '(' expr ')'
```

Implementation hint: each statement has a **shape declaration table** (the statement name, the list of positional parameters with types, optional keywords, whether it opens a block, which blocks may use it). The shared parser reads this table, so adding a new statement is mostly adding one row to the table and one handler function in each Host.

---

## 16. Version and compatibility

- `GEA_VERSION <number>` is required on the first statement line. The interpreter rejects a file with a higher version than it supports.
- Adding a new statement within the same major version is backward compatible. Changing the meaning of an existing statement raises the version.
- Each job records the **language version**, the **product version** and the **version of each builder** so it can be repeated.

---

## 17. Open decisions and risks

| # | Issue | Tentative choice in the draft | To settle |
|---|--------|--------------------------|----------|
| Q1 | Does `INCLUDE` share variables with the parent file or is it isolated | **Settled: share variables** (section 20) | Nothing left |
| Q2 | Array indexes start at 0 | From 0 | Confirm |
| Q3 | `FOR ... TO` includes the last value | Included | Confirm |
| Q4 | A hidden field takes the default value and can still be read | As described in section 7.2 | Confirm |
| Q5 | A parameter created by FORM is always synchronized as a User Parameter | Always synchronized | Confirm |
| Q6 | Does a catch block roll back its half-done changes | Not guaranteed (section 9.7) | **Needs verification with Inventor** |
| Q7 | The source of the appearance library (`SET_APPEARANCE`) | By name in the configured library | Choose where to store it |
| Q8 | The specific Inventor API names for attributes, transactions, appearances | Not determined | Check against the Inventor 2027 documentation when implementing |

Main risks:
- **Statements growing too early**: implement only P0 until Level 1 runs on both Hosts.
- **The two Hosts drifting apart**: every statement must have clear behavior on both Hosts (the table in section 14) and a comparison test suite.
- **A manifest that drifts from the real builder**: keep the manifest and the builder in the same project, with a test that checks the manifest matches the builder signature.
- **Script authors hitting confusing errors**: error messages have a code, line and column from the start.

---

## 18. Next things to do right away

1. Confirm Q2 to Q5, agree or change the draft.
2. Read `tank-level1.gea` and adjust the syntax if it feels unnatural (this is step D0).
3. Write the **shape declaration table of the P0 statements** (section 15) as data.
4. Build the lexer, parser and semantic step (types, units, blocks, manifest) with a suite of valid and invalid test files.
5. Build the **FORM block interpreter** (a pure function, the result as in section 7.3), wired into the form evaluation endpoint of the API.
6. Build the simulation Host, then the Inventor Host for P0, and the `Tank`, `Cover` builders with their manifests.

---

## 19. Values that travel with CADFlow (two-way export/import)

When the CADFlow web exports an order, the `.gea` file holds the **script that ran** and, at the end of the file, a **comment** block recording the values that were set. This is not new language syntax: every line of the block starts with `!@`, and `!` is already a comment (section 2), so the lexer, parser and add-in ignore it. The file still opens and runs everywhere a `.gea` can be used.

```
! ---------------------------------------------------------------------------
! CADFlow export: job AJ-20261108-112028-7e4c, product transformer
! Ordinary .gea tools ignore the lines below; CADFlow reads them back on import.
!@CADFLOW 1
!@JOB AJ-20261108-112028-7e4c
!@PRODUCT transformer 1
!@SCRIPT_SHA256 72d2ef08...
!@VALUE TR_TankLength 2400
!@VALUE TR_RadiatorCount 4
```

| Line | Meaning |
|------|---------|
| `!@CADFLOW 1` | The marker and the version of the block. Without this line the file is treated as a plain `.gea` with no values attached |
| `!@JOB <id>` | The job that created the file (for reference only) |
| `!@PRODUCT <code> <version>` | The product and its version on the server |
| `!@SCRIPT_SHA256 <hex>` | The SHA-256 of the script **without this block**, normalized: LF line endings, exactly one newline at the end, UTF-8 without BOM. Used to know whether the values still match the script |
| `!@VALUE <name> <number>` | The value of one numeric FORM parameter, a real number with a decimal point, **in the display unit of the field** (exactly the unit declared in `USER_INPUT_PARAM`, for example `mm`, `deg`). `INTEGER` is a whole number. `CHECKBOX_PARAM` uses `1`/`0` |
| `!@TEXT <name> "<string>"` | The value of a `RADIOBUTTON_PARAM` field or a string input field. The string is written as JSON (escapes `\"`, `\\`, `\n`, `\uXXXX`) so it fits on one line; Unicode characters such as non-ASCII letters are kept as they are |

Rules:
- The block is always at the end of the file. Exporting a file that already has a block replaces the old block, it does not double it.
- Import reads `!@VALUE` as the initial value of the form in place of the default written in `USER_INPUT_PARAM`. A name that is not in the FORM of the script is skipped and reported; a value that violates `MIN`/`MAX`/`ASSERT` shows as an error on the form like a hand-typed one.
- A mismatching `SCRIPT_SHA256` (the script was edited after the export) is not an error: import still runs, with a warning that the values may no longer fit the script.
- Reference source: `be/Cadflow.Api/GeaExport.cs` (`Build`, `ParseBlock`, `StripBlock`).

### 19.1. Running an imported script on the web

- The server recompiles the script and computes the FORM before accepting the order (it does not trust the browser); the worker does it once more when it builds.
- Only `TARGET PART` is accepted, a file of at most 256 KB, at most 200 fields on the form.
- Each FORM or BUILD run executes at most **1,000,000 statements** (`GeaInterpreter.MaxSteps`, error code `GEA5001`), in addition to the limit of 10,000 iterations per `FOR`/`WHILE`. This way nested loops cannot hang the server or the worker.
- The language has no statement to run external programs or read/write files; BUILD can only call the builders in the catalog (section 12) on the new Part of the job.

---

## 20. `INCLUDE lib:` (reusable components)

Reuses a piece of script (a parameter group in FORM and build steps in BUILD) in another script without copying. A library is a `.gea` file in `Scripts/lib/<name>.gea` (for example `tank-base-cover.gea`: the "Tank, base and cover" group of the power transformer).

```
! Library (Scripts/lib/tank-base-cover.gea)       ! Script that uses the library
GEA_VERSION 1                                      BEGIN_FORM
PRODUCT_NAME "Tank, base and cover"                  INCLUDE lib:tank-base-cover   ! inserts the FORM body of the library
PARAM_PREFIX TR_                                     BEGIN_GROUP "Radiators" ... END_GROUP
TARGET PART                                        END_FORM
BEGIN_FORM ... END_FORM                            BEGIN_BUILD
BEGIN_BUILD ... END_BUILD                            INCLUDE lib:tank-base-cover   ! inserts the BUILD body of the library
                                                     ... the build steps of the parent script ...
                                                   END_BUILD
```

- `INCLUDE lib:<name>` stands **alone on a line, inside** `BEGIN_FORM` or `BEGIN_BUILD` (a trailing `!` comment is allowed). In FORM it inserts the FORM block body of the library, in BUILD it inserts the BUILD block body. If the library has no matching block, nothing is inserted. The position and order are decided by the parent script.
- **Shared variables** (decision Q1/D6): the content is inserted straight into the parent script, so both sides see and can set every variable and parameter. Declaring the same name twice is error `GEA2003`. A library should declare its own variables and state in the comment at the top of the file which variables the parent script may use.
- **Prefix**: the header of the library is not inserted, but its `PARAM_PREFIX` is added to the list of valid prefixes of the parent script. `PARAM_PREFIX` accepts one or several prefixes (`PARAM_PREFIX GT_ TR_`); a parameter is valid if it starts with one of them.
- **Expansion before the lexer**: a preprocessing step (`GeaIncludes.Expand`) replaces the INCLUDE lines with the library content, before the lexer runs. The expanded copy is an ordinary script (no INCLUDE left, and the `PARAM_PREFIX` line lists the library prefixes too), so it can be recompiled on its own.
- **Diagnostics**: the line number is always the line of the original file; an error inside a library has a message that starts with `<name>.gea:` and the line number in the library. New codes: `GEA1010` INCLUDE outside a block; `GEA1011` invalid library name (only `A-Z a-z 0-9 _ -`, at most 64 characters, no path); `GEA1012` library not found or there is no loader; `GEA1013` nested more than 8 levels; `GEA1014` a cycle; `GEA1015` the same library INCLUDEd twice in the same kind of block.
- Limit of the expander: it works line by line, so it does not recognize a multi-line `/* ... */` block comment that contains `INCLUDE`.

### 20.1. Where libraries are loaded from

| Where | How it is found |
|-----|----------|
| Server (import, create job) | `Scripts/lib/<name>.gea` next to the executable (`GeaLibraries`). The server expands **before storing**: `ScriptStore` stores and the worker receives the expanded copy, so the worker needs no library, and **Export .gea** returns the expanded copy (no INCLUDE line left). |
| Inventor add-in (Open .gea) | `<file folder>/lib/<name>.gea`, then `<file folder>/<name>.gea`, then `Scripts/lib` next to the add-in (`GeaLibraryResolver`). |

To add a new library: put `Scripts/lib/<name>.gea` (the same header as a normal script, the FORM/BUILD blocks optional). The library ships with the server and the add-in thanks to the settings in `Cadflow.Api.csproj` and `InventorAddin.Core.csproj`.

Source code: `InventorAddin.Core/Gea/GeaIncludes.cs`, `GeaLibraryResolver.cs`; `GeaScript.Compile(text, resolveLib, fileName)`.

### 20.2. Comment convention for libraries

A library file is downloaded by users to read, so it must explain itself:

- The first paragraph of the comment at the top of the file (up to an empty `!` line) is a short description, shown on the card in the Components page.
- Every **statement paragraph** (consecutive lines, with no blank line between them) in the `BEGIN_FORM` and `BEGIN_BUILD` blocks has at least one comment line saying what the step does (the meaning, formula, why).
- Comments are in English, like the existing scripts.

The `ComponentCommentsTests` test applies this convention to every file in `Scripts/lib`, so a library added later must also explain itself.

### 20.3. Component catalog on the web

The **Components** tab (`#/components`) lists the libraries in `Scripts/lib` (designed in `docs/superpowers/specs/2026-11-10-component-catalog-design.md`). Each card has a description, shared variables, a parameter table and three buttons:

| Button | What it does |
|-----|--------|
| Order this component | Fetches the starter script, sends it through `POST /api/gea/import` and opens the order form to build the component on its own as a Part |
| Download starter script (.gea) | Downloads a script skeleton with two `INCLUDE` lines, every step explained, places to add your own parameters and build steps; edit it, then Import it again |
| Download library (.gea) | Downloads the library file itself, with every step explained |

API (read-only): `GET /api/components`; `GET /api/components/{name}/gea`; `GET /api/components/{name}/starter?productName=&prefix=` (defaults `New product` and `MY_`; an invalid name or a wrong prefix returns 400, a missing library returns 404). Source code: `be/Cadflow.Api/Components.cs` (`ComponentCatalog`, `ComponentStarter`), `fe/src/pages/Components.jsx`.
