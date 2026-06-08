; VeloStream Windows Installer script for Inno Setup
; Run via Wine on Linux:
;   wine "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" VelosCCS_Setup.iss

#define MyAppName "Velos Content Creation Suite"
#define MyAppVersion "4.0.5"
#define MyAppPublisher "VelosCCS"
#define MyAppURL "https://cliptool.app"
#define MyAppExeName "VelosCCS.exe"

[Setup]
AppId={{7E8B9C1D-2F3A-4B5C-6D7E-8F9A0B1C2D3E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=.\installer_build
OutputBaseFilename=VelosCCS_Windows_Setup_v{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardResizable=yes
WizardImageFile=.\installer_assets\WizardImageFile.png
WizardSmallImageFile=.\installer_assets\WizardSmallImageFile.bmp
SetupIconFile=.\installer_assets\SetupIcon.ico
DisableProgramGroupPage=yes
CloseApplications=yes
AppMutex=VelosCCSUpdateMutex
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main exported game — Godot .NET (requires .NET 8 Desktop Runtime)
Source: "..\app_exports\windows\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; .NET assemblies (data_{appname}_{platform}_{arch}/)
Source: "..\app_exports\windows\data_VelosCCS_windows_x86_64\*"; DestDir: "{app}\data_VelosCCS_windows_x86_64"; Flags: ignoreversion recursesubdirs createallsubdirs

; FFmpeg sidecar DLLs (needed for video processing)
Source: "..\app_exports\windows\avcodec-60.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app_exports\windows\avfilter-9.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app_exports\windows\avformat-60.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app_exports\windows\avutil-58.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app_exports\windows\libgdffmpeg.windows.template_release.x86_64.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app_exports\windows\swresample-4.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app_exports\windows\swscale-7.dll"; DestDir: "{app}"; Flags: ignoreversion

; Standalone ffmpeg/ffprobe for export encoding (downloaded by build script)
Source: ".\installer_sidecar\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: ".\installer_sidecar\ffprobe.exe"; DestDir: "{app}"; Flags: ignoreversion

; yt-dlp for YouTube downloading (downloaded by build script)
Source: ".\installer_sidecar\yt-dlp.exe"; DestDir: "{app}"; Flags: ignoreversion

; VC++ Redistributable for llama-cli and ffmpeg on fresh Windows installs
Source: ".\installer_sidecar\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: IsVcRedistNeeded

; .NET 8 Desktop Runtime (required for Godot .NET exports)
Source: ".\installer_sidecar\dotnet8-desktop-runtime-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: IsDotNet8Needed

; Whisper worker — must be published for win-x64 before building installer
Source: ".\WhisperWorker_published\*"; DestDir: "{app}\WhisperWorker_published"; Flags: ignoreversion recursesubdirs createallsubdirs


; Third-party licenses
Source: "LICENSE-THIRD-PARTY.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Visual C++ Redistributable..."; Flags: skipifdoesntexist skipifsilent; Check: IsVcRedistNeeded
Filename: "{tmp}\dotnet8-desktop-runtime-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET 8 Desktop Runtime..."; Flags: skipifdoesntexist skipifsilent; Check: IsDotNet8Needed
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsVcRedistNeeded: Boolean;
var
  Version: String;
begin
  Result := False;
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', Version) then
    if not RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', Version) then
      Result := True;
end;

function IsDotNet8Needed: Boolean;
var
  Names: TArrayOfString;
  i: Integer;
begin
  Result := True;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.Desktop.App', Names) then
    for i := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[i], 1, 2) = '8.' then
        Result := False;
end;

function GetUserProfile(Param: String): String;
begin
  Result := GetEnv('USERPROFILE');
end;

[UninstallRun]
; Remove ALL user data the app created at runtime (exports, AI models, config, etc.)
; Uses {code:GetUserProfile} to get %USERPROFILE% since {userprofile} isn't available in [UninstallDelete]
Filename: "{cmd}"; Parameters: "/C ""rmdir /s /q ""{code:GetUserProfile}\VelosCCS"" 2>nul & rmdir /s /q ""{code:GetUserProfile}\.config\velosccs"" 2>nul & rmdir /s /q ""{code:GetUserProfile}\.cache\velosccs"" 2>nul & rmdir /s /q ""{code:GetUserProfile}\.local\share\VelosCCS"" 2>nul & rmdir /s /q ""{code:GetUserProfile}\.local\share\velosccs"" 2>nul & rmdir /s /q ""{userappdata}\VelosCCS"" 2>nul & rmdir /s /q ""{userappdata}\Godot\app_userdata\VelosCCS"" 2>nul"""; Flags: runhidden; RunOnceId: "VelosCCS.UninstallCleanup"
