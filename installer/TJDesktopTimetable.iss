; TJDesktopTimetable 安装包脚本（Inno Setup 6，Unicode，UTF-8 BOM）
;
; 调用方式（本机 .tools/build-installer.ps1 或 CI）：
;   ISCC.exe installer\TJDesktopTimetable.iss /DSourceDir=<publish 目录> /DMyAppVersion=1.3.1 /DOutputDir=<输出目录>
;
; 规格（2026-09-17 与用户逐条确认）：
;   1. per-machine 装到 Program Files（安装时一次 UAC）
;   2. 向导给四个勾选项：开始菜单 / 桌面快捷方式 / 开机自启 / 装完启动（默认全勾）
;   3. 没有 WebView2 时只提示、不阻止安装（只有内置登录窗口依赖它）
;   4. 卸载保留 %APPDATA%\TJDesktopTimetable 数据，完成页打印路径
;   5. 不做代码签名（只给熟人发；以后公开发布再买证书）
;   6. 覆盖安装时若挂件正在跑，先问一句再关掉（文件被占用会让复制失败）

#ifndef SourceDir
  #define SourceDir "C:\tjt-tools\publish-test"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.3.1"
#endif
#ifndef OutputDir
  #define OutputDir "C:\tjt-tools\setup"
#endif

[Setup]
; AppId 一旦发布就不能改（升级/卸载靠它认人）
AppId={{8E1B7C93-6D2A-4F58-9C3E-2B7A5D4E1F60}
AppName=TJDesktopTimetable
AppVerName=TJDesktopTimetable {#MyAppVersion}
AppVersion={#MyAppVersion}
AppPublisher=gzy31007
AppPublisherURL=https://github.com/gzy31007/TJDesktopTimetable
AppSupportURL=https://github.com/gzy31007/TJDesktopTimetable
DefaultDirName={autopf}\TJDesktopTimetable
DefaultGroupName=TJDesktopTimetable
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; 允许命令行/向导改安装范围：默认仍是 Program Files（要 UAC），
; 自动化验收用 /CURRENTUSER 装到 %LOCALAPPDATA%\Programs 免提权跑同一套逻辑
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TJDesktopTimetable-v{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SourceDir}\Assets\app.ico
UninstallDisplayIcon={app}\Tjt.App.exe
UninstallDisplayName=TJDesktopTimetable {#MyAppVersion}
AllowNoIcons=yes
; 覆盖安装 / 卸载时用 Restart Manager 关掉占用文件的进程（挂件）
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenu";   Description: "创建开始菜单快捷方式"
Name: "desktopicon"; Description: "创建桌面快捷方式"
Name: "autostart";   Description: "开机自动启动（之后可在设置里关掉）"
Name: "runapp";      Description: "安装完成后立即启动"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TJDesktopTimetable";          Filename: "{app}\Tjt.App.exe"; Tasks: startmenu
Name: "{group}\卸载 TJDesktopTimetable";     Filename: "{uninstallexe}";    Tasks: startmenu
Name: "{autodesktop}\TJDesktopTimetable";    Filename: "{app}\Tjt.App.exe"; Tasks: desktopicon

; 开机自启：与应用内「设置 → 常规 → 开机自启」写的是同一个值名，两边不会打架
; （应用自己写时也总是给 exe 路径加引号，格式一致）
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "TJDesktopTimetable"; ValueData: """{app}\Tjt.App.exe"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Tjt.App.exe"; Description: "启动 TJDesktopTimetable"; \
    Flags: nowait postinstall skipifsilent; Tasks: runapp

[Code]
const
  WebView2KeyW6432 = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2KeyNative = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ WebView2 Runtime 在不在（HKLM 两种视图 + HKCU 都查一遍） }
function WebView2Installed(): Boolean;
var
  Pv: String;
begin
  Result :=
    RegQueryStringValue(HKLM, WebView2KeyW6432, 'pv', Pv) or
    RegQueryStringValue(HKLM, WebView2KeyNative, 'pv', Pv) or
    RegQueryStringValue(HKCU, WebView2KeyNative, 'pv', Pv);
end;

{ 挂件是否正在运行（tasklist + find 的退出码就是答案） }
function AppRunning(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq Tjt.App.exe" | find /I "Tjt.App.exe" >NUL',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ 覆盖安装前：挂件占着 Tjt.App.exe，不问就关会让人莫名其妙 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if AppRunning() then
  begin
    if MsgBox('检测到 TJDesktopTimetable 正在运行。安装需要先关闭它，现在关闭吗？',
              mbConfirmation, MB_YESNO) = IDYES then
      Exec(ExpandConstant('{cmd}'), '/C taskkill /IM Tjt.App.exe /F', '',
           SW_HIDE, ewWaitUntilTerminated, Code)
    else
      Result := '安装已取消：请先从托盘图标右键退出 TJDesktopTimetable，然后重试。';
  end;
end;

{ 装完检查 WebView2：只提示，不阻止（粘贴浏览器请求那条路不需要它） }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed()) then
    MsgBox('没有检测到 Microsoft Edge WebView2 Runtime。' + #13#10 + #13#10 +
           '它是「登录学校并获取课表」那个内置浏览器窗口的依赖，缺了它只能用「粘贴一条浏览器请求」导入课表。' + #13#10 + #13#10 +
           'Win11 自带；Win10 可从 Microsoft 官网免费安装（Evergreen Runtime）。',
           mbInformation, MB_OK);
end;

{ 卸载后：数据是我们故意留下的，得把路径说清楚 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('TJDesktopTimetable 已卸载。' + #13#10 + #13#10 +
           '你的课表、设置与登录信息仍保留在：' + #13#10 +
           ExpandConstant('{userappdata}\TJDesktopTimetable') + #13#10 + #13#10 +
           '想彻底清干净就把这个文件夹一并删掉（重装后它会自动重新生效）。',
           mbInformation, MB_OK);
end;
