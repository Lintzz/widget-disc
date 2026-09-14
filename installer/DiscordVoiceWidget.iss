; ---------------------------------------------------------------------------
; Instalador do Discord Voice Widget (Inno Setup 6).
;
; Nao rode direto: use installer\build.ps1, que publica o app e passa a versao.
;
; Decisoes:
; - Instalacao por usuario, sem pedir administrador. O app ja guarda tudo no
;   perfil do usuario (%APPDATA%, %LOCALAPPDATA%) e a inicializacao automatica
;   usa HKCU; nao ha motivo para tocar em Program Files.
; - Build dependente do .NET 10 Desktop Runtime (552 KB) em vez de embutir o
;   runtime (~150 MB). O instalador verifica e oferece instalar se faltar.
; - config.json (com o client secret) e o token nunca sao empacotados. Na
;   desinstalacao o usuario escolhe se remove essas configuracoes.
; ---------------------------------------------------------------------------

#define AppName "Discord Voice Widget"
#define AppExe "DiscordVoiceWidget.exe"
#define AppPublisher "Lintz"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; AppId fixo: e o que liga reinstalacoes e atualizacoes a mesma entrada de desinstalacao.
AppId={{F37D9C5C-0CEB-41BC-B6A4-6DB8EED0454A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\DiscordVoiceWidget
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=DiscordVoiceWidget-Setup-{#AppVersion}
SetupIconFile=..\assets\DiscordVoiceWidget.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; O fechamento do app e feito pelo proprio app (--exit), mais limpo que o Restart Manager.
CloseApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "startup"; Description: "Iniciar com o Windows"; GroupDescription: "Opções:"
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Opções:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Mesmo nome e formato que o app usa (StartupRegistration): a caixa "Iniciar com o
; Windows" nas configuracoes reflete esta escolha.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DiscordVoiceWidget"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName} agora"; Flags: nowait postinstall skipifsilent

[Code]
const
  InstanceMutex = 'DiscordVoiceWidget.SingleInstance';
  RuntimeDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

{ ------------------------------------------------------------------------- }
{ .NET 10 Desktop Runtime                                                    }
{ ------------------------------------------------------------------------- }

{ Pela pasta do runtime, nao pelo registro: a chave                          }
{ InstalledVersions\x64\sharedfx nem sempre existe (ausente em maquina com  }
{ o runtime instalado via SDK), o que daria falso "nao instalado".          }
function DesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function TryInstallRuntimeWithWinget(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('winget',
    'install --id Microsoft.DotNet.DesktopRuntime.10 --exact --silent --accept-package-agreements --accept-source-agreements',
    '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) and DesktopRuntimeInstalled();
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if DesktopRuntimeInstalled() then
    exit;

  if SuppressibleMsgBox(
       'O {#AppName} precisa do .NET 10 Desktop Runtime, que não foi encontrado.' + #13#10#13#10 +
       'Instalar agora pelo winget? (pode pedir permissão de administrador)',
       mbConfirmation, MB_YESNO, IDYES) = IDYES then
  begin
    if TryInstallRuntimeWithWinget() then
      exit;
  end;

  if SuppressibleMsgBox(
       'O .NET 10 Desktop Runtime continua ausente.' + #13#10#13#10 +
       'Abrir a página de download? Depois de instalar, rode este instalador de novo.',
       mbError, MB_YESNO, IDYES) = IDYES then
    ShellExec('open', RuntimeDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);

  Result := False;
end;

{ ------------------------------------------------------------------------- }
{ Fechar o app em execucao                                                   }
{ ------------------------------------------------------------------------- }

{ Pede para a instancia aberta encerrar sozinha ("--exit") e espera o mutex  }
{ de instancia unica sumir. Encerrar pelo proprio app fecha a conexao com o  }
{ Discord direito, o que um taskkill nao faz.                                }
procedure CloseRunningApp(const ExePath: String);
var
  ResultCode, Waited: Integer;
begin
  if not CheckForMutexes(InstanceMutex) then
    exit;

  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Waited := 0;
  while CheckForMutexes(InstanceMutex) and (Waited < 5000) do
  begin
    Sleep(200);
    Waited := Waited + 200;
  end;

  { Instancia antiga que nao responde ao sinal (ou fora da pasta instalada). }
  if CheckForMutexes(InstanceMutex) then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := True;
end;

{ ------------------------------------------------------------------------- }
{ Configuracoes do usuario na desinstalacao                                  }
{ ------------------------------------------------------------------------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    exit;

  { Padrao "Nao": reinstalar depois nao exige recriar o app no Discord. Em modo }
  { silencioso a resposta padrao vale, entao nada e apagado sem perguntar.      }
  if SuppressibleMsgBox(
       'Remover também as configurações, credenciais do Discord e logs?' + #13#10#13#10 +
       'Escolha "Não" se pretende reinstalar.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(ExpandConstant('{userappdata}\DiscordVoiceWidget'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\DiscordVoiceWidget'), True, True, True);
  end;
end;
