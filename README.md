# MSPub2PDF
Export Publisher Documents to Archive PDF

## Runtime modes

The single application executable supports several runtime modes, selected with
`--mode`:

| Mode | Command | Elevation | Purpose |
| --- | --- | --- | --- |
| Main (default) | `app.exe` / `app.exe --mode=main` | Never elevated | The GUI / orchestration process. |
| Render worker | `app.exe --mode=worker --pipe=<name>` | Non-elevated | Hosts the Publisher COM renderer behind an IPC pipe. |
| Font worker | `app.exe --mode=font-worker --pipe=<name>` | Elevated (UAC) | Performs privileged font provisioning (Windows capabilities + system-wide fonts). |

The **main application is never elevated and never relaunches itself**. When
system-wide font provisioning is required, it spawns the font worker as an
elevated child (a single UAC prompt per run) and routes all privileged
operations to it over a strongly-typed request/response pipe.

## Font provisioning

Missing fonts referenced by a document are resolved by the
`FontManagementService` coordinator:

1. **Detection** — classify each missing font as a Windows capability, a
   downloadable fallback (Google Fonts / GitHub / direct URL), or unresolvable.
2. **Elevation decision** — if elevated installs are permitted and something
   needs installing, launch the font worker **once** for the whole run.
3. **Provisioning** — install all required Windows capabilities in a single
   batch through the worker; install downloadable fonts system-wide via the
   worker (when elevated) or user-level under `HKCU` (when not).

All privileged execution lives in the elevated worker process; the main process
performs only detection, orchestration, and non-privileged downloads. Operations
are idempotent, carry correlation IDs end to end, support per-request timeouts,
and emit structured (JSON Lines) logs from both processes.

### User settings

* **Automatic Font Installation** — enables/disables provisioning.
* **Allow Elevated Font Installation** — permits launching the elevated worker
  (system-wide installs). When off, only user-level installs are performed.
* **Missing Font Handling** — `Strict` fails a document whose required fonts
  cannot be resolved; `Notify Only` logs the gap and continues.
