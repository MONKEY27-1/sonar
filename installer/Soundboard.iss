; Soundboard installer script for Inno Setup (https://jrsoftware.org/isinfo.php)
;
; BEFORE compiling this script, publish the app first (from src\Soundboard):
;
;     dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
;
; This bundles the .NET runtime into the publish output, so anyone running the
; installer needs nothing pre-installed — no ".NET Desktop Runtime not found"
; errors for end users. It also avoids .NET single-file publishing, which has
; known rough edges with WPF apps specifically, in favor of the plain
; multi-file publish output (which this script packages up as a whole anyway).
;
; That command produces:
;     src\Soundboard\bin\Release\net8.0-windows\win-x64\publish\
;
; which is exactly what [Files] below expects, relative to this script.
;
; Then open this file in Inno Setup and click Compile (or run ISCC.exe on it).
; The finished installer lands in installer\Output\SonarSetup.exe.

#define MyAppName "Sonar"
#define MyAppPublisher "Sonar"
#define MyAppExeName "Soundboard.exe"
#define MyAppURL "https://sonars.netlify.app"
#define MyPublishDir "..\src\Soundboard\bin\Release\net8.0-windows\win-x64\publish"
; Reads the version resource straight off the compiled exe instead of a second
; hardcoded value here, so this can never drift from Soundboard.csproj's <Version>
; (which is what actually stamps the exe's version resource at publish time).
#define MyAppVersion GetVersionNumbersString(MyPublishDir + "\" + MyAppExeName)

[Setup]
; Unique per-app identifier — do not change between versions, or Windows will
; treat upgrades as a totally separate, side-by-side install instead of
; replacing the old one.
AppId={{EFBCCA32-BBF4-4615-A440-E95FAF7FD5EE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL=https://github.com/MONKEY27-1/sonar/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Installs per-user with no admin prompt by default (to %LocalAppData%\Programs);
; only asks for elevation if explicitly run as admin. Matches how most modern
; desktop apps (VS Code, Discord, etc.) install themselves.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=Output
OutputBaseFilename=SonarSetup
SetupIconFile=..\src\Soundboard\Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Launch Sonar automatically when Windows starts"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "sbpackassoc"; Description: "Open .sbpack collection files with Sonar"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; Both entries below live under HKCU, not HKLM/HKCR, matching the per-user,
; no-admin-required install philosophy (PrivilegesRequired=lowest above) —
; a machine-wide registration would need elevation this installer never asks for.
[Registry]
; Startup launch — a plain Run-key value naming the exe directly, removed automatically
; on uninstall via uninsdeletevalue (no separate Startup-folder shortcut to clean up).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

; .sbpack file association — double-clicking one launches Sonar with the file path as its
; one command-line argument, which App.xaml.cs picks up and imports on startup (see
; MainViewModel.ImportCollectionFromPathAsync). uninsdeletekey on the ProgID cleans up the
; whole class tree; the extension's own default value is removed via uninsdeletevalue.
Root: HKCU; Subkey: "Software\Classes\.sbpack"; ValueType: string; ValueName: ""; ValueData: "Sonar.SoundCollection"; Flags: uninsdeletevalue; Tasks: sbpackassoc
Root: HKCU; Subkey: "Software\Classes\Sonar.SoundCollection"; ValueType: string; ValueName: ""; ValueData: "Sonar Sound Collection"; Flags: uninsdeletekey; Tasks: sbpackassoc
Root: HKCU; Subkey: "Software\Classes\Sonar.SoundCollection\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: sbpackassoc
Root: HKCU; Subkey: "Software\Classes\Sonar.SoundCollection\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: sbpackassoc

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; Deliberately NOT deleting %LocalAppData%\Soundboard (settings, library, and
; imported sound files) on uninstall — that's the user's actual data, and
; wiping it out from under them (e.g. if they're just upgrading, or plan to
; reinstall) would be a bad surprise. Uninstalling only removes the app itself.
