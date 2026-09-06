# Built-in tools

## Small core

| Purpose | Tools |
| --- | --- |
| Bind and observe | `list_revit_instances`, `list_documents`, `get_active_context`, `get_capabilities`, `get_request_status`, `get_logs_tail` |
| Execute | `run_csharp`, `run_python`, `execute_batch`, `execute_and_verify` |
| Inspect | `query_elements`, `get_elements`, `get_parameters`, `get_warnings` |
| View and capture | `select_elements`, `zoom_to_elements`, `open_view`, `export_view`, `capture_model` |
| Attach/reload tools | `list_saved_tools`, `run_saved_tool`, `reload_python_provider`, `reload_tool_provider` |

Each model call binds to PID + document session + generation. Dynamic calls declare `read`, `auto`, `manual` or `group`. Batch related work where practical. Reuse request/idempotency IDs only for the same logical operation.

## Get useful facts

`query_elements` supports category, name, type and level filters, optional type elements, and an ascending `after_id` cursor. Default output is identity and relationships. `next_after_id` / `has_more` support continuation. IDs are document-local; this cursor is not a snapshot across concurrent model changes.

Choose fields: `identity`, `instance_parameters`, `type_parameters`, `bounding_boxes`, `geometry`, `relationships`, `worksharing`, `phase`, `design_option`, `materials`. Parameters expose stable IDs, raw/display values, spec/unit metadata and read-only state. `get_parameters` can select parameter IDs. Materials expose geometry material IDs and area/volume; paint is excluded. Geometry is a bounded summary, not a mesh. Projections report omissions and field errors.

## Optional checks and receipts

`execute_and_verify` runs once, regenerates, inspects requested IDs plus IDs from the result's `element_ids` array (configurable), then evaluates only supplied checks. Supported checks: `exists`, `bounds`, `count` (`expected`), `parameter` (`parameter_id`, `expected`, optional `tolerance`). Checks may supply their own `element_ids`. Empty checks mean no assertion verdict. Legacy nonempty `preflights` are rejected.

`rollback_on_failure=true` requires `auto` or `group`. A failed assertion rolls back before commit. Manual code/files cannot receive that guarantee.

Responses carry `receipt`: accepted/start/completion times, queue/execution milliseconds, revision, progress and cancellation state. `receipt.details` adds model outcome, observed changes, notices and diagnostics. Change counts include observed transaction events; they are not a net model diff. Non-atomic batches report outcomes per step and `partial_possible` overall.

`get_request_status` works while a script runs. Use `after_revision` and `wait_ms` (up to 30 seconds) to wait for change. `request_cancel=true` cancels queued work or requests a cooperative checkpoint. Readiness describes observed queue/coordinator/idle state, not a guaranteed diagnosis. Terminal ledger history is bounded and is lost on restart; missing status does not prove non-execution.

## PNG helper views

One `capture_model` tool accepts these presets:

| Preset | Output |
| --- | --- |
| `building` | Four NSEW elevations and four corner axonometrics; two 2×2 sheets |
| `elevations` / `axo` | The corresponding four-view sheet |
| `floors` | One axonometric per selected floor, sheets of up to four |
| `element` | Context close-up, optional isolated element/components, horizontal and longitudinal middle cuts |
| `horizontal` | A straight horizontal cut through the selected scope |
| `vertical` | A straight long- or short-side cut through the selected scope |

Options: `element_ids`, `level_ids`, `margin_m`, `cut_fraction` (default middle), `axis` (`long`/`short`), `include_isolated`, `pixel_size`, `output_directory`. Return values include actual image paths, view IDs, frames and labelled contact sheets. Up to two sheets also arrive as inline MCP images; other files remain available by path. `inline_images=false` returns paths only.

Explicit selection controls the bounds. Otherwise grids hint at the building envelope and physical elements determine its height/extents. Linked models and terrain are excluded from automatic scope. Use an explicit selection for multiple buildings. Floors combine level/host assignment and story intersection for spanning elements; neighboring floor slabs do not cover the inspected story. Element orientation follows the family/curve where available, with a footprint-axis fallback.

Elevations are orthographic 3D projections relative to **project north**, not drawing elevation markers or true-north views. Cuts are native section views. Images fit their tiles independently; labels state frame dimensions. Visibility is inherited from the document's phase/design-option settings. Helpers preserve the current working view and selection, and use reserved reusable `3XN MCP - …` views. Native section views may be replaced when recaptured.

`export_view` exports existing view IDs as PNG or combined PDF and returns actual files. `open_view` reports whether the requested change is already confirmed; `get_active_context` can confirm a deferred change.
