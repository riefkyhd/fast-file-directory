#define AppName "Fast File Explorer"
#define AppVersion "1.1.6"
#define AppPublisher "Fast File Explorer"
#define AppExeName "FastFileExplorer.exe"

#ifndef PublishDir
  #define PublishDir "..\bin\Release\net9.0-windows\win-x64\publish"
#endif

#ifndef AppIconFile
  #define AppIconFile "..\icon\idemia_new.ico"
#endif

#ifndef DotNetRuntimeInstaller
  #define DotNetRuntimeInstaller "out\runtime\windowsdesktop-runtime-9.0-win-x64.exe"
#endif

[Setup]
AppId={{B7302A64-5898-41F7-B678-FBE2EC647342}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=out\installer
OutputBaseFilename=FastFileExplorer-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#AppIconFile}
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "startupindex"; Description: "Run background indexing at startup"; GroupDescription: "Startup"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#DotNetRuntimeInstaller}"; DestDir: "{tmp}"; DestName: "windowsdesktop-runtime-installer.exe"; Flags: deleteafterinstall; Check: NeedsWindowsDesktopRuntime

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FastFileExplorerIndex"; ValueData: """{app}\{#AppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: startupindex

[Run]
Filename: "{tmp}\windowsdesktop-runtime-installer.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft .NET Desktop Runtime..."; Flags: waituntilterminated runhidden; Check: NeedsWindowsDesktopRuntime
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function StartsWith(const Text, Prefix: string): Boolean;
begin
  Result := Copy(Text, 1, Length(Prefix)) = Prefix;
end;

function NeedsWindowsDesktopRuntime: Boolean;
var
  Subkeys: TArrayOfString;
  I: Integer;
begin
  Result := True;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Subkeys) then
  begin
    for I := 0 to GetArrayLength(Subkeys) - 1 do
    begin
      if StartsWith(Subkeys[I], '9.0.') or (Subkeys[I] = '9.0') then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;
