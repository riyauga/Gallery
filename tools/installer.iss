; Gallery 安装包脚本（Inno Setup 6.7+）
; 用法：
;   ISCC.exe /DSourceDir="<自包含发布目录>" /DOutDir="<产物目录>" tools\installer.iss
; 由 package-release.ps1 传入统一版本、明确文件清单和输出位置。
; 说明：per-user 安装（PrivilegesRequired=lowest），装到 {autopf} = %LOCALAPPDATA%\Programs，不请求管理员权限。

#define AppName "Gallery"
#ifndef AppVersion
  #error AppVersion must be supplied by tools/package-release.ps1
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

#ifndef FileList
  #error FileList must be supplied by tools/package-release.ps1
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
ChangesAssociations=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
#include FileList

[Registry]
; Register as an available handler only. Never write UserChoice or replace extension defaults.
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Gallery"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\shell\open\command"; ValueType: string; ValueData: """{app}\Gallery.exe"" --view ""%1"""
Root: HKCU; Subkey: "Software\Classes\Gallery.Media"; ValueType: string; ValueData: "Gallery 图片与视频"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Gallery.Media\DefaultIcon"; ValueType: string; ValueData: "{app}\Gallery.exe,0"
Root: HKCU; Subkey: "Software\Classes\Gallery.Media\shell\open\command"; ValueType: string; ValueData: """{app}\Gallery.exe"" --view ""%1"""
Root: HKCU; Subkey: "Software\Gallery\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Gallery"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Gallery\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "查看图片和播放视频"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Gallery"; ValueData: "Software\Gallery\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".jpg"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.jpg\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpg"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".jpeg"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.jpeg\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpeg"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".jfif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.jfif\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jfif"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".jpe"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.jpe\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpe"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".png"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.png\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".png"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".gif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.gif\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".gif"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".webp"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.webp\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webp"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".bmp"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.bmp\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".bmp"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".tif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.tif\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tif"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".tiff"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.tiff\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tiff"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".avif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.avif\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avif"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".heic"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.heic\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".heic"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".heif"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.heif\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".heif"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".ico"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.ico\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ico"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".jxl"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.jxl\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jxl"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".psd"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.psd\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".psd"; ValueData: "Gallery.Media"
Root: HKCU; Subkey: "Software\Classes\Applications\Gallery.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "Gallery.Media"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Gallery\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "Gallery.Media"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Gallery.exe"; Check: not WizardNoIcons
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Gallery.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Gallery.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not SaveStringToFile(ExpandConstant('{app}\distribution-kind.txt'), 'installed', False) then
      RaiseException('Cannot save installation type.');
end;
