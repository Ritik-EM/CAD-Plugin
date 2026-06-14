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
    EXCHANGE_DIR_DEFAULT = 'C:\Users\Public\AtlasAltium';
    PART_CODE_PARAM      = 'AtlasPartCode';
    // The bridge is installed here by installer\BuildAltium.cmd.
    BRIDGE_EXE_REL       = '\Atlas\Altium\AtlasAltiumBridge.exe';   // under %LOCALAPPDATA%

{ ---------- small helpers ---------- }

function ExchangeDir: String;
var env: String;
begin
    env := GetEnvironmentVariable('ATLAS_ALTIUM_DIR');   { SPIKE: confirm GetEnvironmentVariable is exposed; else hardcode }
    if env <> '' then Result := env
    else Result := EXCHANGE_DIR_DEFAULT;
end;

function BridgeExePath: String;
begin
    Result := GetEnvironmentVariable('LOCALAPPDATA') + BRIDGE_EXE_REL;
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
    Result := '';
    for i := 0 to Project.DM_ParameterCount - 1 do
    begin
        p := Project.DM_Parameters(i);
        if SameText(p.DM_Name, PART_CODE_PARAM) then
        begin
            Result := p.DM_Value;
            Exit;
        end;
    end;
end;

procedure WritePartCode(Project: IProject; const code: String);
begin
    // Add/update a PROJECT-level parameter so the part code travels inside the .PrjPcb.
    // SPIKE: confirm ObjectKind=Project (not Document) actually adds a project parameter
    //        that round-trips after SaveObject; the process name historically implies a doc.
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
// Returns the OutJob's output base folder (where artifacts land) via outBaseFolder.
procedure RunAllOutputs(const outJobPath, projFolder: String; var outBaseFolder: String);
var ini: TIniFile; grp, idx: Integer; mediumKey, typeKey, name, ctype, basePath: String;
    outJobDoc: IServerDocument;
begin
    outBaseFolder := projFolder + 'Project Outputs\';   // default; refined from INI below

    outJobDoc := Client.OpenDocument('OUTPUTJOB', outJobPath);
    if outJobDoc <> nil then
    begin
        Client.ShowDocument(outJobDoc);
        outJobDoc.Focus;                                 { SPIKE: Focus may be required for the run verbs }
    end;

    ini := TIniFile.Create(outJobPath);   // .OutJob is plain INI text
    try
        // base output path (relative subfolder under the project)
        basePath := ini.ReadString('PublishSettings', 'OutputBasePath1', '');
        if basePath <> '' then outBaseFolder := projFolder + basePath + '\';

        // iterate OutputGroup1..N / OutputMedium1..M
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
                        sourceLines, artifactLines, warnings: TStringList);
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

{ ---------- 7: launch the bridge ---------- }

procedure LaunchBridge(const manifestPath: String);
var sh: Variant; cmd: String; rc: Integer;
begin
    cmd := '"' + BridgeExePath + '" --manifest "' + manifestPath + '"';
    // SPIKE: confirm CreateOleObject is available in DelphiScript on the target version.
    // WScript.Shell.Run(cmd, windowStyle, waitOnReturn) is the standard Windows idiom.
    sh := CreateOleObject('WScript.Shell');
    rc := sh.Run(cmd, 1, True);   // 1 = normal window, True = wait for the bridge to finish
    if rc <> 0 then
        ShowWarning('Atlas bridge exited with code ' + IntToStr(rc) +
                    '. See result.json / errors.log in the exchange dir.');
end;

procedure ReportResult(const resultPath: String);
var rs: TStringList;
begin
    if FileExists(resultPath) then
    begin
        rs := TStringList.Create;
        try
            rs.LoadFromFile(resultPath);
            ShowInfo('Atlas check-in result:'#13#10 + rs.Text);
        finally
            rs.Free;
        end;
    end;
end;

{ ============================================================================
  Entry point — bind this to a menu/toolbar button.
  ============================================================================ }
procedure AtlasCheckin;
var
    Workspace: IWorkspace;
    Project: IProject;
    partCode, projName, comment, dir, manifestPath, resultPath, outFolder, outJobPath: String;
    sourceLines, artifactLines, warnings: TStringList;
    i: Integer;
begin
    Workspace := GetWorkspace;
    if Workspace = nil then begin ShowError('No Altium workspace.'); Exit; end;
    Project := Workspace.DM_FocusedProject;
    if Project = nil then begin ShowError('Open a PCB project first.'); Exit; end;

    projName := Project.DM_ProjectFileName;

    // 1 + 2
    CompileAndSaveAll(Project);

    // 3
    partCode := EnsurePartCode(Project);
    if partCode = '' then begin ShowWarning('No part code entered — aborting check-in.'); Exit; end;

    comment := InputBox('Check-in comment', 'Optional comment for this check-in:', '');

    sourceLines  := TStringList.Create;
    artifactLines := TStringList.Create;
    warnings     := TStringList.Create;
    try
        // 4
        CollectSourceFiles(Project, sourceLines);
        for i := 0 to sourceLines.Count - 1 do
            if Pos('|managed', sourceLines[i]) > 0 then
                warnings.Add('A managed (Altium 365) library component is not bundled — needs the Workspace to re-open.')
            else if Pos('|database', sourceLines[i]) > 0 then
                warnings.Add('A database (.DbLib) library is bundled but needs the external DB to re-open.');

        // 5 — run outputs and harvest. The template OutJob ships beside this script;
        //     SPIKE: confirm the deployed OutJob path. We look next to the project first.
        outJobPath := ExtractFilePath(Project.DM_ProjectFullPath) + 'Atlas_Template.OutJob';
        if FileExists(outJobPath) then
        begin
            RunAllOutputs(outJobPath, ExtractFilePath(Project.DM_ProjectFullPath), outFolder);
            HarvestArtifacts(outFolder, artifactLines);
        end
        else
            warnings.Add('Atlas_Template.OutJob not found beside the project — no artifacts generated (REQ 2 skipped).');

        // 6
        dir := ExchangeDir;
        if not DirectoryExists(dir) then ForceDirectories(dir);
        manifestPath := dir + '\manifest.json';
        resultPath   := dir + '\result.json';
        if FileExists(resultPath) then DeleteFile(resultPath);

        WriteManifest(manifestPath, 'checkin', partCode, projName, comment,
                      sourceLines, artifactLines, warnings);

        // 7
        LaunchBridge(manifestPath);
        ReportResult(resultPath);
    finally
        sourceLines.Free;
        artifactLines.Free;
        warnings.Free;
    end;
end;
