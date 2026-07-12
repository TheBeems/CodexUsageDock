; Per-user installer for the Codex Usage Dock Command Palette extension.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Platform
  #define Platform "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installers"
#endif

#define AppName "Codex Usage Dock"
#define AppExeName "CodexUsageDock.exe"
#define AppPublisher "Mathijs Beemsterboer"
#define AppUrl "https://github.com/TheBeems/CodexUsageDock"

[Setup]
AppId={{35b848eb-6ef7-4b5d-9f8b-49b5614abb48}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\CodexUsageDock
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=CodexUsageDock-{#AppVersion}-{#Platform}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

#if Platform == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Classes\CLSID\{{35b848eb-6ef7-4b5d-9f8b-49b5614abb48}\LocalServer32"; ValueType: string; ValueData: """{app}\{#AppExeName}"" -RegisterProcessAsComServer"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\CLSID\{{35b848eb-6ef7-4b5d-9f8b-49b5614abb48}"; ValueType: string; ValueData: "CodexUsageDock"; Flags: uninsdeletekey

[Run]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Microsoft.CmdPal.UI.exe /F >nul 2>&1"; Flags: runhidden; StatusMsg: "Refreshing Command Palette..."; Check: CmdPalIsRunning

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Microsoft.CmdPal.UI.exe /F >nul 2>&1"; Flags: runhidden; RunOnceId: "RefreshCommandPalette"

[Code]
function CmdPalIsRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Microsoft.CmdPal.UI.exe" | find /I "Microsoft.CmdPal.UI.exe" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;
