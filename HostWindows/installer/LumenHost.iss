; Inno Setup script for Lumen Host (Windows).
; Build:  iscc /DAppVersion=1.0.42 /DSourceDir=C:\path\to\publish /O"C:\out" LumenHost.iss
; SourceDir must contain LumenHost.exe, ffmpeg.exe and any other published files.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win"
#endif

#define AppName "Lumen Host"
#define AppExe  "LumenHost.exe"

[Setup]
AppId={{6B0C5E0A-3D43-4C1B-9E5B-4C7A1D8F2A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Lumen
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\lumen.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputBaseFilename=Lumen-Host-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "vdd"; Description: "Install the SudoVDA virtual display driver (needed for Extend mode; adds the driver certificate to the trusted store)"; GroupDescription: "Optional:"; Flags: unchecked
Name: "autostart"; Description: "Start Lumen Host with Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LumenHost"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{cmd}"; Parameters: "/c ""{app}\drivers\sudovda\install.bat"""; WorkingDir: "{app}\drivers\sudovda"; Flags: runhidden waituntilterminated; StatusMsg: "Installing virtual display driver..."; Tasks: vdd; Check: DriverBundled
; Firewall rules (inbound UDP: 47900 = Lumen stream, 5353 = mDNS discovery). Delete first so reinstalls stay idempotent.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lumen Host UDP 47900"""; Flags: runhidden; StatusMsg: "Configuring firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lumen Host UDP 5353"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lumen Host UDP 47900"" dir=in action=allow protocol=UDP localport=47900 profile=any"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lumen Host UDP 5353"" dir=in action=allow protocol=UDP localport=5353 profile=any"; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lumen Host UDP 47900"""; Flags: runhidden; RunOnceId: "FwDel47900"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lumen Host UDP 5353"""; Flags: runhidden; RunOnceId: "FwDel5353"

[Code]
function DriverBundled: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\drivers\sudovda\install.bat'));
end;
