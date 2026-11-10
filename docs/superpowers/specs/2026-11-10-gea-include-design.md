# Design: `INCLUDE lib:` to reuse a component in a .gea script

Date: 2026-11-10. Status: the design direction was approved in chat.

## Goal

Reuse a component (for example the "Tank, base and cover" group of `transformer.gea`: the form + the Base/Tank/Cover build steps) in another .gea script without copying code. Changing the library changes every script that uses it. This is P1 `INCLUDE lib:<name>` in `sa-docs/gea-language-spec.md` and answers Q1/D6: **share variables with the parent script**.

## Out of scope

Libraries uploaded by users on the web, choosing components through the UI, namespaces (`AS Tank`), inserting an `.ipt` part into an assembly (`ADD_COMPONENT`), include nesting deeper than 8 levels.

## Approach: expand the text before the lexer

A preprocessing step replaces each `INCLUDE lib:<name>` line with the matching content of the library. The lexer, parser, checker and interpreter do not change (except the prefix part in section 3). This was chosen over an include on the AST because the risk is much lower, and over copying a block of code because a change to the library must propagate.

### 1. Syntax

A library is a file `Scripts/lib/<name>.gea`: it has a header (`GEA_VERSION`, `PARAM_PREFIX`, ...), a `BEGIN_FORM ... END_FORM` block and a `BEGIN_BUILD ... END_BUILD` block (both optional).

The parent script writes `INCLUDE lib:<name>` **inside** a block:

```
BEGIN_FORM
    INCLUDE lib:tank-base-cover      ! inserts the FORM body of the library (parameter group, ASSERT)
  BEGIN_GROUP "Radiators" ...
END_FORM

BEGIN_BUILD
    INCLUDE lib:tank-base-cover      ! inserts the BUILD body of the library
    ... radiators, conservator ...
END_BUILD
```

In `BEGIN_FORM` this line inserts the FORM block body of the library, in `BEGIN_BUILD` it inserts the BUILD block body. The position and order are decided by the parent script. If the library has no matching block, nothing is inserted. `INCLUDE` outside a block is an error.

### 2. Shared variables

The content is inserted straight into the parent script, and sees and sets every variable. A duplicate name (parameter, variable) is reported by the existing checker. `DECLARE_VARIABLE` in the library lives in the BUILD scope of the parent.

### 3. Parameter prefix

The header of the library is not inserted (only `PARAM_PREFIX` is taken). `GeaProgram` gets `ExtraParamPrefixes`; the checker (`GEA2030` in `GeaChecker.cs:337` and `:393`) accepts a name that starts with the prefix of the parent **or** a prefix in the list. The first library is `tank-base-cover` with `PARAM_PREFIX TR_` (parameter names unchanged), and `transformer.gea` switches to using `INCLUDE`. `TransformerParams.cs`, old jobs and the form do not change.

### 4. API (Core, no Inventor dependency)

```csharp
public sealed record GeaExpansion(string Text, IReadOnlyList<GeaSourceLine> Lines, IReadOnlyList<string> LibPrefixes, IReadOnlyList<GeaDiagnostic> Diagnostics);
public sealed record GeaSourceLine(string File, int Line);          // a line of the expanded text -> (file, original line)
public static class GeaIncludes { public static GeaExpansion Expand(string text, Func<string, string?> resolveLib); }
```

`GeaInterpreter.Compile(string text)` gets a parameter `Func<string,string?>? resolveLib = null`; an `INCLUDE` with no resolver is an error. Compile calls `Expand`, parses the expanded copy, sets `LibPrefixes`, then maps the `Line` of every diagnostic through `Lines`. An error inside a library carries the file name (`tank-base-cover.gea:23`).

### 4a. Safeguards

- The library name matches `^[A-Za-z0-9_-]{1,64}$`, with no path or `..`.
- Maximum depth 8 and cycle detection (A includes B includes A).
- A library may be included only once per kind of block (the second time is an error, because it would declare things twice).
- A library that is not found is an error with a line, not an exception.

### 5. Where libraries are loaded from

- **Server**: `Scripts/lib/*.gea` is copied to the run folder (like `transformer.gea`, linked from `InventorAddin.Core/Scripts/lib`). The resolver reads `AppContext.BaseDirectory/Scripts/lib/<name>.gea`. When a script with `INCLUDE` is imported or a job is created from it, the server expands **before** storing: `ScriptStore` stores and the worker receives **the expanded copy**, so the Inventor worker needs no library, and the hash is computed on the expanded copy.
- **Add-in**: the resolver looks in the `Scripts/lib` that ships with the add-in, then in the folder of the open file.
- **Export .gea** on the web returns the expanded copy (no `INCLUDE` line is kept). The file runs on its own anywhere.

### 6. Compatibility

After the change, `transformer.gea` must give the **same** parameter list, default values, labels, `MIN/MAX/ASSERT` rules and the same BUILD command sequence as the version without `INCLUDE`. This is the main acceptance condition.

## Testing

In `be/Cadflow.Api.Tests` (the server already links the Gea code):

1. Expansion goes to the right block for FORM and BUILD; a library missing a block inserts nothing.
2. `transformer.gea` using `INCLUDE` is equivalent to the original (compare the parameter list, defaults, labels, the validate result for a few value sets, the BUILD commands).
3. Errors: `INCLUDE` outside a block, a missing library, a name containing `/` or `..`, a cycle, a second include, exceeding the depth, no resolver.
4. Line mapping: an error in the library reports the file name and the line in the library; an error in the parent script reports the right original line even with insertions above it.
5. Prefix: a script with another prefix (for example `GT_`) that includes a `TR_` library is valid; an unknown parameter still gets `GEA2030`.
6. Server: importing a script with `INCLUDE` gives the right form and stores the expanded copy; creating a job produces a `WorkItem.ScriptText` with no `INCLUDE`.

## Documentation

Update `sa-docs/gea-language-spec.md` (the INCLUDE section, close Q1, the P1 section) and `docs/smartassembly-to-inventor-mapping.md` (D6 settled).

## Risks

- The Inventor add-in cannot be built/tested on this machine (no Inventor). The add-in part (the resolver by folder) can only be built, not run; the expansion logic is in Core so it is tested on the server.
- A `TR_` parameter appearing in the script of another product could cause confusion. Renaming to a neutral prefix would need changes to `TransformerParams.cs` and old jobs, so it is left for later.

## Changes made during implementation (versus the original design)

- **`PARAM_PREFIX` accepts several prefixes** (`PARAM_PREFIX GT_ TR_`). The expanded copy is stored and recompiled without a resolver, so the `PARAM_PREFIX` line of the parent script must list the prefixes of the libraries too, otherwise the parameters `TR_...` get `GEA2030`. `GeaIncludes.Expand` rewrites this line itself.
- The INCLUDE error codes are `GEA1010`-`GEA1015` (the syntax range), not `GEA3xxx` (that range is for value checks).
- `tank-base-cover` declares `hTank`, `hCover`, `hTop` itself and uses its own reference variable `tbcBody`; the parent script does not declare those variables again. The three assignments `hTank/hCover/hTop` now come after `PROCESSING_BOX_START` (the result is unchanged, checked with the equivalence test on the simulation Host).
- The equivalence test of `transformer.gea` runs both versions on `GeaSimulationHost` and compares the command traces, so it does not depend on the order of statements.
