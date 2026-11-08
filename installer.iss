; Inno Setup script for MusicRPC
#define AppName "MusicRPC"
#define AppVersion "1.2.4"
#define AppPublisher "murphydev"
#define AppExe "MusicRPC.exe"

[Setup]
AppId={{6B1E3C52-7A0D-4F0B-9C6E-2D5F8E4A9B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=installer
OutputBaseFilename=MusicRPC-Setup-{#AppVersion}
SetupIconFile=app.ico
WizardSmallImageFile=installer-assets\small-55.bmp,installer-assets\small-64.bmp,installer-assets\small-72.bmp,installer-assets\small-80.bmp,installer-assets\small-96.bmp,installer-assets\small-110.bmp
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern dark
WizardImageAlphaFormat=defined
WizardImageFile=installer-assets\wizard-164.bmp,installer-assets\wizard-192.bmp,installer-assets\wizard-246.bmp,installer-assets\wizard-328.bmp,installer-assets\wizard-355.bmp
AppMutex=MusicRPC.SingleInstance.5F2B
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
russian.WelcomeLabel1=Добро пожаловать в MusicRPC
russian.WelcomeLabel2=Программа покажет друзьям в Discord, что вы слушаете: трек, обложку, прогресс и кнопку «Слушать в …».%n%nSpotify, Яндекс Музыка и SoundCloud определяются автоматически, ничего настраивать не нужно.%n%nНажмите «Далее», чтобы продолжить.
russian.FinishedHeadingLabel=MusicRPC установлен
russian.FinishedLabel=Включите музыку при запущенном Discord — статус появится сам. Программа живёт в трее и сама проверяет обновления.
english.WelcomeLabel1=Welcome to MusicRPC
english.WelcomeLabel2=Show your friends on Discord what you are listening to: track, cover art, progress and a «Listen on …» button.%n%nSpotify, Yandex Music and SoundCloud are detected automatically.%n%nClick Next to continue.
english.FinishedHeadingLabel=MusicRPC is installed
english.FinishedLabel=Start some music with Discord running and your status will appear. The app lives in the tray and checks for updates by itself.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Запускать вместе с Windows"; GroupDescription: "Дополнительно:"

[Files]
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppName}"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\MusicRPC"
