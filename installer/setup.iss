; Telegram Media Downloader - Inno Setup Script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)

#define MyAppName "Telegram Media Downloader"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "TelegramMediaDownloader"
#define MyAppURL "https://github.com/il90il90/TelegramMediaDownloader"
#define AppExe "TelegramMedia.Service.exe"
; Name of the Windows Service used by versions <= 1.0.x (removed on upgrade).
#define LegacyServiceName "TelegramMediaDownloader"

[Setup]
AppId={{B4F2D8A1-3E5C-4F9A-B7D6-8E1C2A3F5B4D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
; Per-user install: no admin rights, no UAC. {autopf} maps to {localappdata}\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\output
OutputBaseFilename=TelegramMediaDownloader-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\TelegramMedia.Service\app.ico
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Single self-contained desktop app (Blazor dashboard hosted in-process + tray)
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
; Launch automatically at login (replaces the old Windows Service auto-start).
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#AppExe}"
; Desktop shortcut (optional, task-gated)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Launch the app after install.
Filename: "{app}\{#AppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the app if running.
Filename: "taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden

[Code]
// On install/upgrade: remove the legacy Windows Service and kill old processes.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Tear down the old service-based install (versions <= 1.0.x), if present.
    Exec('sc.exe', 'stop {#LegacyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete {#LegacyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Kill the old tray app and any running instance of this app.
    Exec('taskkill.exe', '/f /im TelegramMedia.Tray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
end;
