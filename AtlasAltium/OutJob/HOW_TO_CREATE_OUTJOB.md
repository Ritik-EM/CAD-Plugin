# Creating the Atlas OutJob (REQ 2: BOM / PDF / Gerber / STEP)

An OutputJob (`.OutJob`) **must be created inside Altium** — a hand-authored file fails to
open with *"Unrecognized OutputJob Document Version"*, because Altium validates an internal
version header and bindings to the project's documents that only its editor writes.

The check-in script (`AtlasCheckin.pas`) looks for a file named **`Atlas_Template.OutJob`
beside the project's `.PrjPcb`**. Create it once per project like this:

## Steps (in Altium Designer)

1. With your project open: **File → New → Output Job File**.
2. Add these four outputs (click the "Add New …" links in each section):
   - **Report Outputs → Bill of Materials → Project** → in *Change* set the format to **CSV**
     (or XLSX).
   - **Fabrication Outputs → Gerber Files**, and **Fabrication Outputs → NC Drill Files**.
   - **Export Outputs → Export STEP → PCB Document**. (Requires the **STEP/MBASTEP**
     extension to be installed/enabled.)
   - **Documentation Outputs → Schematic Prints** (this is the PDF).
3. For each output, set its **Output Container**:
   - Put the BOM, Gerbers, NC Drill, and STEP into a **Folder Structure** container
     (a "Generated Files" container).
   - Put Schematic Prints into a **PDF** container (a "Publish" container).
   - Point both containers' output path at the project's **`Project Outputs`** folder
     (the script harvests that folder by file extension).
4. **Enable** each output — the dot/arrow next to it must be green. The script runs every
   enabled container; disabled outputs are ignored, and the script **cannot** enable them
   from code.
5. **Save As** → save it **next to the `.PrjPcb`** with the exact name **`Atlas_Template.OutJob`**.

## How the script uses it

On check-in the script opens this OutJob, runs every enabled container
(`WorkspaceManager:GenerateReport` for generated files, `WorkspaceManager:Print` for the PDF),
then scans the output folder and classifies files by extension:
`.csv/.xlsx → bom`, `.pdf → pdf`, `.step/.stp → step`, Gerber/drill → `gerber`. Those become
the `artifacts[]` in the manifest and are uploaded to Atlas alongside the project.

> Until this file exists beside the project, check-in still works — it just skips REQ 2 and
> uploads only the project source (REQ 1), with a warning.
