; Gallery 安装包脚本（Inno Setup 6.7+）
; 用法：
;   ISCC.exe /DSourceDir="<自包含发布目录>" /DOutDir="<产物目录>" tools\installer.iss
;   /DAppVersion="1.0.1" 可覆盖版本号；默认值与 Gallery.csproj 的 Version 保持一致。
; 说明：per-user 安装（PrivilegesRequired=lowest），装到 {autopf} = %LOCALAPPDATA%\Programs，不请求管理员权限。

#define AppName "Gallery"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppPublisher
  #define AppPublisher "riyauga"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{7E3B9F2A-4C1D-4A6E-9B7F-2D5C8A1E4F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/riyauga/Gallery
AppSupportURL=https://github.com/riyauga/Gallery/issues
AppUpdatesURL=https://github.com/riyauga/Gallery/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\assets\Gallery.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayIcon={app}\Gallery.exe
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Gallery.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Gallery.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Gallery.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
