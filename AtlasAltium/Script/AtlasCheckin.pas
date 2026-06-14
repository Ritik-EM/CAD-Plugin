{ ============================================================================
  AtlasCheckin.pas  -  Atlas PLM check-in for Altium Designer (DelphiScript)

  The in-Altium half of the Atlas Altium integration. Bind AtlasCheckin to a
  menu/toolbar button (DXP > Customize). On click it:

    1. Compiles the focused project (DM_Compile) so the doc/component model is current.
    2. Force-saves every open document (don't trust the Modified flag).
    3. Reads the Atlas part code from the 'AtlasPartCode' project parameter
       (prompts + writes it back on first run).
    4. Enumerates the project's source files (.PrjPcb + .SchDoc + .PcbDoc + libs).
    5. Runs every enabled container in the template OutJob (BOM/PDF/Gerber/STEP),
       then harvests the output folder.
    6. Writes manifest.json to the exchange dir.
    7. Launches AtlasAltiumBridge.exe, which does auth + the actual upload to Atlas
       (reusing AtlasCadCore). Reads back result.json and reports.

  All HTTPS/auth/S3 logic lives in the C# bridge, NOT here. This script does only
  Altium work + local file I/O + launching the bridge.

  SPIKE markers flag calls that the feasibility research could not fully verify
  offline and that must be confirmed on the target Altium Designer version.
  ============================================================================ }

const
    // Fixed, user-independent location for the bridge EXE + exchange files
    // (manifest.json / result.json / *.tree.json). DelphiScript does NOT expose
    // GetEnvironmentVariable, so we hardcode rather than read %LOCALAPPDATA%/%PUBLIC%.
    // installer\BuildAltium.cmd installs AtlasAltiumBridge.exe into this same folder.
    EXCHANGE_DIR    = 'C:\Users\Public\AtlasAltium';
    PART_CODE_PARAM = 'AtlasPartCode';

{ ---------- small helpers ---------- }

function ExchangeDir: String;
begin
    Result := EXCHANGE_DIR;
end;

function BridgeExePath: String;
begin
    Result := EXCHANGE_DIR + '\AtlasAltiumBridge.exe';
end;

// JSON string escaping (backslash, quote, control chars). Altium paths are full of '\'.
function JsonEsc(const s: String): String;
var i: Integer; c: Char; r: String;
begin
    r := '';
    for i := 1 to Length(s) do
    begin
        c := s[i];
        case c of
            '\': r := r + '\\';
            '"': r := r + '\"';
            #13: r := r + '\r';
            #10: r := r + '\n';
            #9:  r := r + '\t';
        else
            r := r + c;
        end;
    end;
    Result := r;
end;

function JsonStr(const s: String): String;
begin
    Result := '"' + JsonEsc(s) + '"';
end;

// tiny inline-if for strings (DelphiScript has no ternary). Declared early so
// callers below resolve it (Pascal requires declaration before use).
function IfThenStr(cond: Boolean; const a, b: String): String;
begin
    if cond then Result := a else Result := b;
end;

function LowerExt(const path: String): String;
begin
    Result := LowerCase(ExtractFileExt(path));
end;

{ ---------- 1+2: compile and save ---------- }

procedure CompileAndSaveAll(Project: IProject);
var i: Integer; doc: IDocument; sdoc: IServerDocument;
begin
    Project.DM_Compile;   // populate/refresh the logical+component model

    // Save EVERY logical document unconditionally. DoFileSave on a clean doc is a
    // cheap no-op; the Modified flag is unreliable across AD versions, so don't gate on it.
    for i := 0 to Project.DM_LogicalDocumentCount - 1 do
    begin
        doc := Project.DM_LogicalDocuments(i);
        sdoc := Client.GetDocumentByPath(doc.DM_FullPath);
        if sdoc <> nil then
            sdoc.DoFileSave('');   { SPIKE: confirm '' kind saves; some kinds want 'SCH'/'PCB' }
    end;
    // Also save the project file itself.
    sdoc := Client.GetDocumentByPath(Project.DM_ProjectFullPath);
    if sdoc <> nil then sdoc.DoFileSave('');
end;

{ ---------- 3: part code as a project parameter ---------- }

function ReadPartCode(Project: IProject): String;
var i: Integer; p: IParameter;
begin
    // Return the LAST matching parameter (newest wins) so a carry-forward update is
    // read correctly even if a stale duplicate ever lingers.
    Result := '';
    for i := 0 to Project.DM_ParameterCount - 1 do
    begin
        p := Project.DM_Parameters(i);
        if SameText(p.DM_Name, PART_CODE_PARAM) then
            Result := p.DM_Value;
    end;
end;

procedure WritePartCode(Project: IProject; const code: String);
begin
    // A PROJECT-level parameter so the part code travels inside the .PrjPcb. Uses the same
    // DocumentAddParameter call proven to work for the initial set. NOTE: on carry-forward
    // this may append a second AtlasPartCode rather than update in place (we avoid assigning
    // the read-only WSM DM_Value, which DelphiScript may reject). ReadPartCode reads the LAST
    // (newest) match, so the latest revision always wins; any extra entries are cosmetic.
    ResetParameters;
    AddStringParameter('ObjectKind', 'Project');
    AddStringParameter('Name', PART_CODE_PARAM);
    AddStringParameter('Value', code);
    RunProcess('WorkspaceManager:DocumentAddParameter');

    ResetParameters;
    AddStringParameter('ObjectKind', 'Project');
    AddStringParameter('FileName', Project.DM_ProjectFullPath);
    RunProcess('WorkspaceManager:SaveObject');
end;

// Carry-forward: the bridge writes the new root revision to current_part_code.txt after a
// successful check-in. Apply it to the project's AtlasPartCode so the NEXT check-in bumps
// from the latest revision (not the original base). Applied once, then the file is cleared.
procedure ApplyPendingRevision(Project: IProject);
var f, newCode: String; rs: TStringList;
begin
    f := ExchangeDir + '\current_part_code.txt';
    if not FileExists(f) then Exit;
    newCode := '';
    rs := TStringList.Create;
    try
        rs.LoadFromFile(f);
        if rs.Count > 0 then newCode := Trim(rs[0]);
    finally
        rs.Free;
    end;
    if newCode <> '' then
    begin
        WritePartCode(Project, newCode);
        ShowInfo('Atlas: project advanced to the latest revision ' + newCode +
                 ' (carried forward from the previous check-in).');
    end;
    DeleteFile(f);
end;

function EnsurePartCode(Project: IProject): String;
var code: String;
begin
    code := ReadPartCode(Project);
    if code = '' then
    begin
        code := InputBox('Atlas part code',
            'No Atlas part code is stored for this project.'#13#10 +
            'Enter the 10-character Atlas part code to bind it to:', '');
        code := Trim(code);
        if code <> '' then
            WritePartCode(Project, code);
    end;
    Result := code;
end;

{ ---------- 4: enumerate source files ---------- }

// Classify a logical document by extension into a manifest "role".
function RoleForDoc(const path: String): String;
var ext: String;
begin
    ext := LowerExt(path);
    if ext = '.prjpcb' then Result := 'project'
    else if ext = '.schdoc' then Result := 'schematic'
    else if ext = '.pcbdoc' then Result := 'pcb'
    else if (ext = '.schlib') or (ext = '.pcblib') or (ext = '.intlib') or
            (ext = '.dblib')  or (ext = '.pcbdwf') then Result := 'library'
    else Result := 'other';
end;

// Collect project source files into a TStringList of "path|role|bucket" lines.
// Layer 1: logical documents (guaranteed-complete for project source).
// Layer 3: belt-and-suspenders folder scan (catches anything Add-to-Project missed).
//
// SPIKE/TODO: Layer 2 (per-component library resolution via IIntegratedLibraryManager
//   .FindComponentLibraryPath, bucketing managed/database components) is the part that
//   needs on-machine verification. A sketch is in the comments below; for now the folder
//   scan picks up file-based libs that live in/under the project folder. External libs on
//   a search path outside the project folder are NOT yet captured -> see spike #3.
procedure CollectSourceFiles(Project: IProject; lines: TStringList);
var i: Integer; doc: IDocument; folder: String; sr: TSearchRec; seen: TStringList;
begin
    seen := TStringList.Create;
    seen.Duplicates := dupIgnore;
    seen.Sorted := True;

    // root project file
    lines.Add(Project.DM_ProjectFullPath + '|project|file');
    seen.Add(LowerCase(Project.DM_ProjectFullPath));

    // logical documents
    for i := 0 to Project.DM_LogicalDocumentCount - 1 do
    begin
        doc := Project.DM_LogicalDocuments(i);
        if seen.IndexOf(LowerCase(doc.DM_FullPath)) < 0 then
        begin
            lines.Add(doc.DM_FullPath + '|' + RoleForDoc(doc.DM_FullPath) + '|file');
            seen.Add(LowerCase(doc.DM_FullPath));
        end;
    end;

    {  SPIKE #3 sketch — per-component library resolution:
       ILM := IntegratedLibraryManager;
       for each schematic, iterate components, call:
         libPath := ILM.FindComponentLibraryPath(libRef, sourceLibName, ...);
       if libPath resolves to a file  -> add 'libPath|library|file'
       if it resolves to a vault GUID  -> add '|library|managed' (+ warning)
       if AvailableLibraryType = eLibDatabase -> add '<dblib>|library|database' (+ warning)
       Confirm the exact ILM method names + managed-detection props in EDPInterfaces.pas. }

    // folder scan of the project directory tree (libs/outputs living beside the project)
    folder := ExtractFilePath(Project.DM_ProjectFullPath);
    if FindFirst(folder + '*.*', faAnyFile, sr) = 0 then
    begin
        repeat
            if (sr.Attr and faDirectory) = 0 then
            begin
                if (LowerExt(sr.Name) = '.schlib') or (LowerExt(sr.Name) = '.pcblib') or
                   (LowerExt(sr.Name) = '.intlib') or (LowerExt(sr.Name) = '.dblib') then
                    if seen.IndexOf(LowerCase(folder + sr.Name)) < 0 then
                    begin
                        lines.Add(folder + sr.Name + '|library|file');
                        seen.Add(LowerCase(folder + sr.Name));
                    end;
            end;
        until FindNext(sr) <> 0;
        FindClose(sr);
    end;

    seen.Free;
end;

{ ---------- 5: run the OutJob and harvest artifacts ---------- }

// Run one OutJob container. type is the OutputMediumN_Type read from the .OutJob INI.
procedure RunOutputContainer(const containerName, containerType: String);
begin
    if SameText(containerType, 'Publish') or SameText(containerType, 'PublishToPDF') then
    begin
        ResetParameters;
        AddStringParameter('Action', 'PublishToPDF');
        AddStringParameter('OutputMedium', containerName);
        AddStringParameter('ObjectKind', 'OutputBatch');
        AddStringParameter('DisableDialog', 'True');     // suppress the pre-publish modal
        AddStringParameter('PromptOverwrite', 'False');
        RunProcess('WorkspaceManager:Print');
    end
    else  // GeneratedFiles: Gerbers, NC drill, BOM export, Export STEP, netlists, P&P
    begin
        ResetParameters;
        AddStringParameter('Action', 'Run');
        AddStringParameter('OutputMedium', containerName);
        AddStringParameter('ObjectKind', 'OutputBatch');
        RunProcess('WorkspaceManager:GenerateReport');
    end;
    // ResetParameters is mandatory before each call — the buffer is not auto-cleared.
end;

// Open the template OutJob, read its containers from the INI, and run each one.
// Run every enabled container in the OutJob. Returns True if the OutJob opened and ran;
// False (caller warns + skips) if the file isn't a valid OutJob. The caller harvests the
// produced files separately (outputs scatter across container subfolders, so we scan the
// whole project tree rather than guess one output folder here).
function RunAllOutputs(const outJobPath: String): Boolean;
var ini: TIniFile; grp, idx: Integer; mediumKey, typeKey, name, ctype: String;
    outJobDoc: IServerDocument;
begin
    Result := False;

    // A hand-authored / wrong-version OutJob makes Altium raise "Unrecognized OutputJob
    // Document Version". Guard so that never crashes the check-in — just skip artifacts.
    outJobDoc := nil;
    try
        outJobDoc := Client.OpenDocument('OUTPUTJOB', outJobPath);
    except
        outJobDoc := nil;
    end;
    if outJobDoc = nil then Exit;   // not a valid OutJob -> caller warns + skips REQ 2

    Client.ShowDocument(outJobDoc);
    outJobDoc.Focus;                                     { SPIKE: Focus may be required for the run verbs }

    ini := TIniFile.Create(outJobPath);   // .OutJob is plain INI text
    try
        // iterate OutputGroup1..N / OutputMedium1..M and run each enabled container
        for grp := 1 to 8 do
            for idx := 1 to 32 do
            begin
                mediumKey := 'OutputMedium' + IntToStr(idx);
                typeKey   := mediumKey + '_Type';
                name  := ini.ReadString('OutputGroup' + IntToStr(grp), mediumKey, '');
                ctype := ini.ReadString('OutputGroup' + IntToStr(grp), typeKey, '');
                if name = '' then Continue;
                if SameText(ctype, 'PublishToWeb') then Continue;   // skip web publish
                RunOutputContainer(name, ctype);
            end;
        Result := True;
    finally
        ini.Free;
    end;
end;

// Classify a produced file by extension into an artifact "kind".
function ArtifactKind(const path: String): String;
var ext: String;
begin
    ext := LowerExt(path);
    if (ext = '.csv') or (ext = '.xls') or (ext = '.xlsx') then Result := 'bom'
    else if ext = '.pdf' then Result := 'pdf'
    else if (ext = '.step') or (ext = '.stp') then Result := 'step'
    else if (ext = '.txt') then Result := 'gerber'   // NC drill is often .txt
    else
    begin
        // Gerber extensions are .g** (e.g. .gtl/.gbl/.gts). Check length FIRST so we
        // never index ext[2] out of range — DelphiScript 'and' is not guaranteed to
        // short-circuit, so don't rely on (Length>=3) guarding ext[2] in one expression.
        Result := '';
        if Length(ext) >= 3 then
            if ext[2] = 'g' then Result := 'gerber';
    end;
end;

// Recursively scan the output folder for produced artifacts -> "path|kind" lines.
procedure HarvestArtifacts(const folder: String; lines: TStringList);
var sr: TSearchRec; full, kind: String;
begin
    if not DirectoryExists(folder) then Exit;
    if FindFirst(folder + '*.*', faAnyFile, sr) = 0 then
    begin
        repeat
            if (sr.Name = '.') or (sr.Name = '..') then Continue;
            full := folder + sr.Name;
            if (sr.Attr and faDirectory) <> 0 then
                HarvestArtifacts(full + '\', lines)
            else
            begin
                kind := ArtifactKind(full);
                if kind <> '' then lines.Add(full + '|' + kind);
            end;
        until FindNext(sr) <> 0;
        FindClose(sr);
    end;
end;

{ ---------- 6: write the manifest ---------- }

procedure WriteManifest(const path, operation, partCode, projName, comment: String;
                        sourceLines, artifactLines, warnings, scanDirs: TStringList);
var js: TStringList; i: Integer; parts: TStringList;
begin
    // NOTE: DelphiScript does NOT allow a nested procedure to see an enclosing
    // procedure's local variable ("Can't access top level variable"), so we call
    // js.Add(...) directly instead of a nested AddRaw helper.
    js := TStringList.Create;
    parts := TStringList.Create;
    try
        js.Add('{');
        js.Add('  "schema_version": 1,');
        js.Add('  "operation": ' + JsonStr(operation) + ',');
        js.Add('  "part_code": ' + JsonStr(partCode) + ',');
        js.Add('  "project_name": ' + JsonStr(projName) + ',');
        js.Add('  "comment": ' + JsonStr(comment) + ',');

        // source_files[]
        js.Add('  "source_files": [');
        for i := 0 to sourceLines.Count - 1 do
        begin
            parts.Clear;
            parts.Delimiter := '|'; parts.StrictDelimiter := True;
            parts.DelimitedText := sourceLines[i];
            while parts.Count < 3 do parts.Add('');
            js.Add('    { "path": ' + JsonStr(parts[0]) +
                   ', "role": ' + JsonStr(parts[1]) +
                   ', "bucket": ' + JsonStr(parts[2]) + ' }' +
                   IfThenStr(i < sourceLines.Count - 1, ',', ''));
        end;
        js.Add('  ],');

        // artifacts[]
        js.Add('  "artifacts": [');
        for i := 0 to artifactLines.Count - 1 do
        begin
            parts.Clear;
            parts.Delimiter := '|'; parts.StrictDelimiter := True;
            parts.DelimitedText := artifactLines[i];
            while parts.Count < 2 do parts.Add('');
            js.Add('    { "path": ' + JsonStr(parts[0]) +
                   ', "kind": ' + JsonStr(parts[1]) + ' }' +
                   IfThenStr(i < artifactLines.Count - 1, ',', ''));
        end;
        js.Add('  ],');

        // artifact_scan_dirs[] — folders the BRIDGE scans for generated outputs AFTER it
        // waits for Altium's async OutJob generation to finish (the script can't harvest
        // them in time because generation runs in the background).
        js.Add('  "artifact_scan_dirs": [');
        for i := 0 to scanDirs.Count - 1 do
            js.Add('    ' + JsonStr(scanDirs[i]) + IfThenStr(i < scanDirs.Count - 1, ',', ''));
        js.Add('  ],');

        // warnings[]
        js.Add('  "warnings": [');
        for i := 0 to warnings.Count - 1 do
            js.Add('    ' + JsonStr(warnings[i]) + IfThenStr(i < warnings.Count - 1, ',', ''));
        js.Add('  ]');
        js.Add('}');

        js.SaveToFile(path);   { SPIKE: confirm SaveToFile writes UTF-8; if it writes Latin-1, force UTF-8 }
    finally
        parts.Free;
        js.Free;
    end;
end;

{ ---------- 7: hand off to the watcher ---------- }

// DelphiScript on this Altium build can't launch an external EXE (no CreateOleObject/
// ShellExecute), so instead of running the bridge we SIGNAL the always-running watcher:
// write the trigger file AFTER the manifest, so the watcher only ever reads a complete
// manifest. The watcher (AtlasAltiumBridge.exe --watch) picks it up and uploads in the
// background. If the watcher isn't running, warn and fall back to running the EXE manually.
procedure SignalWatcher(const manifestPath: String);
var dir: String; ts: TStringList;
begin
    dir := ExchangeDir;
    ts := TStringList.Create;
    try
        ts.Add('check-in requested');
        ts.SaveToFile(dir + '\request.trigger');
    finally
        ts.Free;
    end;

    if FileExists(dir + '\watcher.alive') then
        ShowInfo('Check-in sent to the Atlas watcher.' + #13#10#13#10 +
                 'It uploads in the background (it first waits for the OutJob outputs to ' +
                 'finish generating, so allow a minute or two). A result dialog will pop ' +
                 'when it''s done.')
    else
        ShowWarning('Request written, but the Atlas watcher does not appear to be running.' +
                    #13#10#13#10 +
                    'Start it (the "Atlas Altium Watcher" Startup shortcut), or run the ' +
                    'bridge once manually:' + #13#10 + BridgeExePath);
end;

// Collect the project's OWN OutJob documents (REQ 2). These are the real, enabled OutJobs
// the project already ships (e.g. "EMS vendor files", "PCB fabrication files"), so we don't
// need a hand-authored Atlas_Template.OutJob.
procedure CollectProjectOutJobs(Project: IProject; paths: TStringList);
var i: Integer; doc: IDocument;
begin
    for i := 0 to Project.DM_LogicalDocumentCount - 1 do
    begin
        doc := Project.DM_LogicalDocuments(i);
        if SameText(ExtractFileExt(doc.DM_FullPath), '.OutJob') then
            paths.Add(doc.DM_FullPath);
    end;
end;

// After generating outputs, close the leftover focused doc (Gerber generation opens a
// CAMtastic preview that can throw a modal save prompt and stall an unattended run).
// Best-effort — never let cleanup break the check-in.
procedure CleanupAfterOutputs;
begin
    try
        ResetParameters;
        AddStringParameter('ObjectKind', 'FocusedDocument');
        RunProcess('WorkspaceManager:CloseObject');
    except
    end;
end;

{ ============================================================================
  Entry point — bind this to a menu/toolbar button.
  ============================================================================ }
procedure AtlasCheckin;
var
    Workspace: IWorkspace;
    Project: IProject;
    partCode, projName, comment, dir, manifestPath, resultPath: String;
    sourceLines, artifactLines, warnings, outJobPaths, scanDirs: TStringList;
    i, k: Integer;
begin
    Workspace := GetWorkspace;
    if Workspace = nil then begin ShowError('No Altium workspace.'); Exit; end;
    Project := Workspace.DM_FocusedProject;
    if Project = nil then begin ShowError('Open a PCB project first.'); Exit; end;

    // Guard: the FOCUSED project must be a PCB project (.PrjPcb). When a script tab is the
    // active document, DM_FocusedProject can resolve to the script project (.PrjScr) instead,
    // which has no .PrjPcb -> a near-empty manifest -> the bridge fails with "no project file".
    if not SameText(ExtractFileExt(Project.DM_ProjectFullPath), '.PrjPcb') then
    begin
        ShowError('The focused project is "' + Project.DM_ProjectFileName + '", not a PCB project.' +
                  #13#10#13#10 +
                  'Click your PCB project (the .PrjPcb, e.g. STARK_4.2.2) or one of its open ' +
                  'documents in the Projects panel to focus it, then run AtlasCheckin again.');
        Exit;
    end;

    projName := Project.DM_ProjectFileName;

    // 0 — carry forward the revision from the previous check-in (if any) so the project's
    //     AtlasPartCode tracks the latest revision before we read it below.
    ApplyPendingRevision(Project);

    // 1 + 2
    CompileAndSaveAll(Project);

    // 3
    partCode := EnsurePartCode(Project);
    if partCode = '' then begin ShowWarning('No part code entered — aborting check-in.'); Exit; end;

    comment := InputBox('Check-in comment', 'Optional comment for this check-in:', '');

    sourceLines  := TStringList.Create;
    artifactLines := TStringList.Create;
    warnings     := TStringList.Create;
    scanDirs     := TStringList.Create;
    try
        // 4
        CollectSourceFiles(Project, sourceLines);
        for i := 0 to sourceLines.Count - 1 do
            if Pos('|managed', sourceLines[i]) > 0 then
                warnings.Add('A managed (Altium 365) library component is not bundled — needs the Workspace to re-open.')
            else if Pos('|database', sourceLines[i]) > 0 then
                warnings.Add('A database (.DbLib) library is bundled but needs the external DB to re-open.');

        // 5 — FIRE the project's OWN OutJobs (REQ 2). Altium generates outputs
        //     ASYNCHRONOUSLY, so we do NOT harvest here (the files aren't written yet —
        //     that's why an in-script scan found nothing). Instead we record the folder to
        //     scan; the bridge waits for generation to finish and harvests it (a separate
        //     process can wait without blocking Altium). For STEP, add an "Export STEP ->
        //     PCB Document" output to an OutJob and enable it.
        outJobPaths := TStringList.Create;
        try
            CollectProjectOutJobs(Project, outJobPaths);
            if outJobPaths.Count = 0 then
                warnings.Add('No OutputJob in the project — no artifacts generated (REQ 2 skipped).')
            else
            begin
                for k := 0 to outJobPaths.Count - 1 do
                    if not RunAllOutputs(outJobPaths[k]) then
                        warnings.Add('Could not run OutJob ''' + ExtractFileName(outJobPaths[k]) + ''' (REQ 2 partial).');
                // The bridge harvests artifacts from here after generation completes.
                scanDirs.Add(ExtractFilePath(Project.DM_ProjectFullPath));
            end;
        finally
            outJobPaths.Free;
        end;

        // 6
        dir := ExchangeDir;
        if not DirectoryExists(dir) then ForceDirectories(dir);
        manifestPath := dir + '\manifest.json';
        resultPath   := dir + '\result.json';
        if FileExists(resultPath) then DeleteFile(resultPath);

        WriteManifest(manifestPath, 'checkin', partCode, projName, comment,
                      sourceLines, artifactLines, warnings, scanDirs);

        // 7 — hand off to the background watcher (it uploads; result dialog pops when done)
        SignalWatcher(manifestPath);
    finally
        sourceLines.Free;
        artifactLines.Free;
        warnings.Free;
        scanDirs.Free;
    end;
end;
