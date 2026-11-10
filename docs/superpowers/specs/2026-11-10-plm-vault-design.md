# PLM vault (design)

**Goal.** A place to keep generated results for good, with structure and versions, in a Docs-style UI. Job results under `jobs/` are deleted by the retention policy; the vault is not.

**Decisions.**
- Separate front-end `fe-plm/` (Vite + React + Radix Themes, built with base `/plm/`); the back end is shared (`be/Cadflow.Api/Vault/`, same PostgreSQL, same R2/disk).
- Structure: one folder per job named `<product>-<job id>` with fixed sub-folders `image` (iso and section views), `drawing` (pdf/dwg), `model` (ipt/stp). Users may create other folders and upload any file (including `.rvt`; there is no step that generates Revit files).
- Version + State only: a new version of the same file name resets the state to In Work. Transitions: In Work → Under Review → (In Work | Approved); Approved → (In Work | Released); Released is final (check in a new version to continue).
- Saving copies the bytes (R2 `CopyObject`, or file copy) to `vault/{item}/v{n}/{name}`; it never points back to `jobs/`. Saving the same job twice is a no-op (`source_job_id` on the current version).
- No login: "who" is a free-text name kept in the browser. Upload goes through the server (request body = file, up to 512 MB) rather than a presigned PUT, to keep one code path for R2 and disk.

**Not in scope.** Login and roles, multi-step approval workflow, check-out locking, BOM/assembly relations, generating Revit files, 3D viewer, a real Windchill.

**Files.** `Vault/VaultStore.cs` (tables, rules), `Vault/VaultFiles.cs` (bytes), `Vault/VaultEndpoints.cs` (API), `be/Cadflow.Api.Tests/VaultTests.cs`; front-end `fe-plm/src/` (`App.jsx`, `pages/Files|Item|Search.jsx`); the Save to PLM button is in `fe/src/pages/Job.jsx`.
