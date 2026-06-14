# Artifacts (REQ 2): which OutJobs run, and adding STEP

On check-in the Atlas script runs **the OutJobs already in your project** (under
*Settings → Output Job Files*, e.g. `EMS vendor files…`, `PCB fabrication files…`). You do **not**
need a hand-authored `Atlas_Template.OutJob` — a hand-authored file fails to open anyway
(*"Unrecognized OutputJob Document Version"*), because Altium validates an internal version
header its editor writes.

For each project OutJob the script runs every **enabled** container, then scans that OutJob's
output folder and classifies produced files by extension:

| Produced file | Atlas slot |
|---|---|
| `.csv` / `.xlsx` (BOM) | companion + feeds the BOM |
| `.pdf` (Schematic Prints) | `reference_documents.2d` |
| `.step` / `.stp` (whole-board) | `reference_documents.3d` |
| Gerber / NC drill | companions (fabrication) |

So the only thing to set up is making sure the outputs you want are **enabled** (green dot/arrow)
in your OutJobs — the script can't enable them from code.

## Adding STEP (most projects don't have it yet)

`reference_documents.3d` only populates if an OutJob produces a `.step`. To add it:

1. Open one of your OutJobs (e.g. `PCB fabrication files…`).
2. **Export Outputs → [Add New Export Output] → Export STEP → PCB Document**.
3. Assign it to a **Folder Structure** output container and point that container at the
   project's output folder.
4. **Enable** the output (green) and **save** the OutJob.

> Requires the **STEP/MBASTEP** installer extension to be enabled in Altium.
> ⚠️ Whole-board STEP on a large board (your `.PcbDoc` is ~15 MB) can stall the writer — the
> same risk class as the CATIA root-assembly STEP hang. Test it and watch for a freeze; if it
> hangs, disable the STEP output and we'll drive it differently.

## Where outputs are harvested from

The script reads each OutJob's `[PublishSettings] OutputBasePath1` and scans that folder
(recursively). If an OutJob writes to a different/secondary path, those files won't be picked up —
point the outputs you care about at the OutJob's primary output folder.
