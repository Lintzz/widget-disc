# Arquitetura e notas técnicas

Documento para quem vai mexer no código. Para instalar e usar, veja o [README](../README.md).

## Como o widget conversa com o Discord

Os dados vêm do **servidor RPC local do Discord**: um named pipe (`\\.\pipe\discord-ipc-0`
a `-9`) que o Discord de desktop abre para programas da mesma máquina. Não há servidor
próprio nem nada injetado no Discord.

1. `DiscordIpcClient` abre o pipe, faz o `HANDSHAKE` com o `client_id` e recebe o `READY`.
2. Na primeira vez, o comando `AUTHORIZE` abre o pedido de autorização dentro do Discord.
   O `code` devolvido é trocado por um token no endpoint OAuth2
   (`https://discord.com/api/oauth2/token`), com o `client_secret` do app do usuário.
3. O token fica em `%APPDATA%\DiscordVoiceWidget\token.dat`, protegido por DPAPI, e é
   renovado sozinho um dia antes de vencer.
4. Com `AUTHENTICATE`, o widget assina `VOICE_CHANNEL_SELECT` e `VOICE_SETTINGS_UPDATE` e,
   dentro de uma call, `VOICE_STATE_*` e `SPEAKING_START`/`SPEAKING_STOP` do canal.

Os escopos usados são `rpc`, `rpc.voice.read`, `rpc.voice.write` e `identify`. O
`rpc.voice.write` libera o `SET_VOICE_SETTINGS`, usado pelo menu para mutar e ensurdecer.
Um token salvo sem algum desses escopos (de antes da 1.0.4) é descartado: como o Discord
recusa `AUTHORIZE` num pipe já autenticado (`4002: Already authenticated`), a sessão cai e
a próxima autoriza num pipe novo.

**Fechar o Discord** não passa pelo RPC, que não tem comando para isso: o widget encerra
os processos `Discord`, `DiscordPTB` e `DiscordCanary` (`DiscordProcess`).

**Abrir o Discord** (clique duplo) também não: o widget roda `Update.exe --processStart
Discord.exe`, o mesmo do atalho do Menu Iniciar. Se o Discord já estiver aberto, ele
recebe o pedido e mostra a própria janela, inclusive saindo da bandeja. Depois o widget
espera a janela principal aparecer, restaura se estiver minimizada (o Discord não faz
isso sozinho) e a leva para o monitor do widget clicado com `SetWindowPlacement`.

### Armadilhas do redirect no fluxo RPC

Dois comportamentos que a documentação oficial não menciona e que custaram duas tentativas:

- **Não envie `redirect_uri` no comando `AUTHORIZE`.** O Discord recusa com
  `Redirect URI cannot be used in the RPC OAuth2 Authorization flow`. Ele usa sozinho o
  primeiro redirect cadastrado no portal. O `redirect_uri` só entra na troca do código por
  token, no endpoint HTTP.
- **Mas o app precisa ter pelo menos um redirect cadastrado.** Sem nenhum, o mesmo
  `AUTHORIZE` falha com o erro oposto: `invalid_request: Missing "redirect_uri" in request`.

> **Por que um app próprio é obrigatório:** os escopos `rpc`, `rpc.voice.read` e `rpc.voice.write` — necessários
> para os eventos `SPEAKING_START` / `SPEAKING_STOP` — só são liberados para o **dono do app**
> e para até 50 contas na lista de *testers*. Como você é o dono, funciona sem pedir nada ao
> Discord. Isso também significa que este widget **não é distribuível publicamente** sem
> aprovação formal do Discord.

## Harness de console (`tools/RpcSpike`)

```bash
dotnet run --project tools/RpcSpike
```

Na primeira execução um modal abre no Discord pedindo autorização — clique em **Authorize**.
O token fica salvo em `%APPDATA%\DiscordVoiceWidget\token.dat` (o mesmo do widget) e não é pedido de novo.

**Critério de sucesso:** entre em uma call e fale. Saída da validação (nomes e IDs trocados):

```
1. Conectando ao pipe
  conectado em \\.\pipe\discord-ipc-0
  READY - conectado como voce (100000000000000001)

4. Estado inicial
[18:44:42] MEU MIC  aberto
  entrou em "Geral" (200000000000000001)
  participantes:
    - Ana         avatar: https://cdn.discordapp.com/avatars/100000000000000002/0a1b2...png?size=64
    - Voce        avatar: https://cdn.discordapp.com/avatars/100000000000000001/3c4d5...png?size=64

[18:45:03.658]   silencio Voce
[18:45:03.921] FALANDO   Voce
[18:45:04.172]   silencio Voce
[18:45:04.200] FALANDO   Ana
```

O único risco técnico real do projeto está resolvido: o resto é interface.

### O que a validação ensinou sobre a UI

Os eventos chegam em **rajadas de 250–300 ms** durante a fala normal — repare no trecho acima,
onde `Voce` alterna três vezes em meio segundo. Ligar o anel verde direto no `SPEAKING_START`
e desligar no `SPEAKING_STOP` produziria uma piscação errática, nada parecida com a voz.
Por isso a biblioteca aplica um *debounce de queda* de ~250 ms: o `SPEAKING_STOP` só apaga o anel
se nenhum `SPEAKING_START` do mesmo usuário chegar nesse intervalo.

### Se algo falhar

| Sintoma | Causa provável |
|---|---|
| `Nenhum pipe discord-ipc-0..9 respondeu` | Discord desktop fechado, ou rodando elevado enquanto o spike não está |
| `Troca do code falhou (400)` | `clientSecret` errado, ou `redirectUri` diferente do cadastrado no portal |
| `Missing "redirect_uri" in request` | O app não tem nenhum redirect cadastrado no portal |
| `Redirect URI cannot be used in the RPC OAuth2 Authorization flow` | O `AUTHORIZE` está enviando `redirect_uri` — ele não pode |
| Erro no `AUTHORIZE` com `code 4100`/scope | Sua conta não é dona do app, ou o escopo não foi aceito |
| Nenhum `SPEAKING_*` aparece | O `SUBSCRIBE` precisa do `channel_id` — confirme que a linha "entrou em ..." apareceu |

Para investigar qualquer resposta inesperada, descomente a linha `ipc.RawFrameReceived`
em `tools/RpcSpike/Program.cs` — ela imprime todo frame cru vindo do Discord.

## Estrutura

```
src/DiscordVoiceWidget.Rpc/     biblioteca de voz, sem dependência de UI
  DiscordIpcClient.cs           transporte: pipe, framing, nonce↔resposta
  VoiceSessionService.cs        máquina de estados, reconexão, debounce da fala
  OAuthTokenStore.cs            token protegido por DPAPI + refresh
  Models/VoiceModels.cs         participante, credenciais, estados, caminhos

src/DiscordVoiceWidget.App/     aplicação WPF
  App.xaml.cs                   ciclo de vida, instância única, sinais, ponte RPC → UI
  Controls/VoiceWidgetView       a pílula (microfone + avatares), usada por todas as janelas
  Windows/TaskbarWidgetWindow    widget embutido na barra (janela filha): posição e visibilidade
  Windows/OverlayWindow          widget sobre os jogos: posição livre, cliques atravessam
  Windows/SettingsWindow         configurações (tema Fluent nativo do WPF)
  Windows/CredentialsWindow      primeira execução: Application ID e Client Secret
  Classes/GlobalHotkeys.cs       atalhos globais (RegisterHotKey)
  Classes/MonitorLocator.cs      monitores por nome de dispositivo
  Classes/NativeMethods.cs       P/Invoke (SetWindowPos, EnumChildWindows, ...)
  Classes/WindowHelper.cs        topmost, no-activate  (portado do FluentFlyout)
  Classes/TaskbarPositionHelper  qual barra usar, âncora e desvio de outros widgets
  Classes/TaskbarClockLocator    relógio da barra secundária via UI Automation
  Classes/ShellEventsWindow.cs   janela oculta que repassa avisos do sistema
  Classes/WidgetSettings.cs      settings.json
  Classes/StartupRegistration    iniciar com o Windows (HKCU\...\Run)
  Classes/FileLog.cs             log diário
  Classes/MemoryTrimmer.cs       devolve memória ociosa ao sistema
  Classes/AvatarCache.cs         avatares em memória e disco
  Classes/DemoCall.cs            call fictícia do modo --demo (prints do README)
  ViewModels/                    estado observável da UI

tools/RpcSpike/                 harness de console sobre a biblioteca
  Program.cs                    consome a mesma API que a UI vai consumir
tools/IconGen/                  gera assets/DiscordVoiceWidget.ico

installer/
  DiscordVoiceWidget.iss        script do Inno Setup
  build.ps1                     publica o app e gera o instalador em artifacts/
```

O harness não é código morto: ele exercita exatamente a API que a interface usa sem WPF no
caminho, o que torna muito mais rápido isolar se um problema é de voz ou de interface.

## API da biblioteca

```csharp
var credentials = DiscordCredentials.LoadOrBootstrap();
await using var session = new VoiceSessionService(credentials);

session.ParticipantsChanged      += participants => { /* redesenha os avatares */ };
session.SpeakingChanged          += (userId, speaking) => { /* liga/desliga o anel */ };
session.SelfVoiceSettingsChanged += (muted, deafened) => { /* ícone do microfone */ };
session.StateChanged             += (state, detail) => { /* mostra/esconde a janela */ };

await session.StartAsync(ct);
```

Os eventos chegam na thread do loop de leitura do pipe — a UI precisa marshalar
para o `Dispatcher`.

## Consumo de recursos

O widget fica aberto o dia todo, junto com o Discord, enquanto o PC roda jogos. O
projeto passou por uma rodada de otimização medida — cada mudança abaixo foi
comparada antes e depois, nesta máquina (Ryzen, 12 threads, Radeon, 2 monitores),
em call com 4 pessoas.

| Métrica | Antes | Depois |
|---|---|---|
| CPU em call (em regime, 1 widget) | 641 ms/min (~1,07% de um núcleo) | **31–125 ms/min** (0,05–0,2%), conforme o quanto se fala |
| Memória privada | 138,8 MB | **~25 MB** |
| Working set (o "Memória" do Gerenciador de Tarefas) | 207,2 MB | **~23 MB** logo após devolver, **~37–42 MB** em uso |
| Handles | 2134 | ~510 |
| Memória e mecanismo de GPU | driver da AMD com dispositivo Direct3D | **nenhum uso** (contadores de GPU do Windows zerados) |
| Pasta publicada | 26 MB | **740 KB** (instalador: 2,3 MB) |
| Fora de call | timer de 1 s rodando | **zero timers, zero hooks** |

### O que mudou, em ordem de impacto

1. **Anel sem animação.** Cada troca do anel com fade de 80/150 ms redesenhava vários
   quadros. Medido por minuto em call: ~4,9 ms de CPU por troca com fade, ~0,7 ms sem.
   Numa call ativa são dezenas de trocas por minuto.
2. **Renderização por software.** Por padrão o WPF abre um dispositivo Direct3D na
   placa de vídeo. Para uma pílula de 200×40 px desenhada só quando muda, isso não
   traz nada — e ainda disputa a GPU com o jogo e, com `AllowsTransparency`, obriga
   cada quadro a voltar da GPU para a CPU. A DLL do driver ainda aparece mapeada
   (o WPF enumera adaptadores na inicialização), mas nenhum dispositivo é criado.
3. **Nada roda fora de call.** Antes, um timer de 1 s varria todas as janelas do
   sistema, consultava o relógio por UI Automation (acordando o explorer) e reafirmava
   o topmost — mesmo com o widget invisível. Agora, fora de call, não existe timer
   nem hook. Em call, os widgets da barra ficam embutidos nela e não precisam
   disputar o "sempre por cima"; mudança de tela, DPI, configuração da barra e
   reinício do explorer chegam como mensagens de janela. Sondagem só para o que o
   Windows não avisa: largura de outro widget (2 s) e o relógio (uma vez por minuto).
   O overlay, que flutua sobre os jogos, reafirma o topo quando o app em primeiro
   plano muda (WinEvent), com rede de segurança a cada 6 s.
4. **Varredura só da faixa topmost.** Janelas por cima da barra precisam ser topmost,
   e as topmost ficam no topo da ordem Z. Descer a partir do topo e parar na primeira
   não-topmost visita 61 janelas em vez de 468: 17,7× mais rápido, mesmo resultado.
5. **Sem WinForms e sem ícone na bandeja.** O `NotifyIcon` do WinForms puxava ~17 MB
   de módulos para dentro do processo. O ícone saiu de vez: o menu fica no próprio
   widget (botão direito) e as configurações também abrem pelo Menu Iniciar.
6. **Memória ociosa devolvida ao sistema** após a inicialização, ao sair de call e ao
   fechar as configurações (no máximo a cada 2 min): coleta agressiva do GC e corte do
   working set. É memória física a mais disponível para o jogo.
7. **Sem alocação por evento de fala.** Buffers de leitura do pipe vêm de um
   `ArrayPool`, eventos são entregues sem copiar o JSON, nomes de evento não viram
   strings novas, e o atraso de desligar o anel usa um timer reaproveitado por pessoa
   em vez de `CancellationTokenSource` + `Task.Delay` a cada evento.
8. **Reconexão sem espera ativa.** Com o Discord fechado, `ConnectAsync(1000)` levava
   1 s e ~26 ms de CPU por pipe inexistente — dez pipes a cada rodada. Com timeout 0 a
   tentativa é instantânea e o resultado é o mesmo.
9. **Runtime enxuto.** GC não concorrente (uma thread a menos), PGO dinâmico desligado,
   JSON gerado em compilação com reflexão desligada, ReadyToRun no publish e alvo
   `net10.0-windows` sem a projeção WinRT de ~25 MB que o app não usa.

### O que foi testado e descartado

- **Modo de globalização invariante** (sem ICU): economizou 0,5 MB de memória privada
  e nada mensurável no working set. Não compensa o risco de o WPF, que consulta
  culturas internamente, se comportar diferente.

### Por que o monitor secundário

Jogos em tela cheia no monitor principal cobrem a barra principal — incluindo
janelas topmost. A barra do segundo monitor continua visível. Se ela não existir
(um monitor só, ou "Mostrar a barra de tarefas em todos os monitores" desligado), o
widget cai automaticamente para a barra principal.

### Embutido na barra

Os widgets da barra são janelas **filhas da própria taskbar** (`WS_CHILD` + `SetParent`),
a mesma técnica do FluentFlyout. A primeira versão flutuava por cima da barra como
janela topmost e piscava ao clicar na barra: a taskbar também é topmost e, ao ser
ativada, subia por cima do widget até ele se reafirmar. Embutido, isso não acontece
(verificado subindo as duas barras para o topo da ordem Z e capturando a tela: os
widgets continuam visíveis).

Consequências:
- **Tela cheia sem detecção:** um jogo em tela cheia que cubra a barra cobre o widget
  junto; a barra do outro monitor, e o widget nela, seguem visíveis.
- **Reinício do explorer:** a barra destruída leva a janela filha junto. O app percebe
  e recria o widget quando a barra nova existe.
- **Avisos do sistema:** janelas filhas não recebem broadcasts (resolução, reinício do
  explorer); uma janela oculta de nível superior (`ShellEventsWindow`) recebe e repassa.
- **Fila de entrada compartilhada:** janela filha de outro processo divide a fila de
  entrada com a thread da barra. A thread de UI do widget só trabalha quando algo muda,
  então não atrasa o explorer.

### Nas duas telas

Com "Barras dos dois monitores", o app mantém uma janela do widget em cada barra —
à esquerda do relógio na secundária e da bandeja (ou do widget vizinho) na principal.
As duas desenham o mesmo estado: há uma única conexão com o Discord.

- Cada janela vive na sua barra: com um jogo em tela cheia no principal, só o widget
  da barra principal fica coberto.
- Sem segundo monitor, a janela da secundária fica escondida em vez de recair na
  principal (onde já está a outra), e aparece sozinha quando o monitor é conectado.
- Custo: cada troca do anel é desenhada nas duas janelas, então o gasto de CPU em
  call dobra em relação a um widget só — ainda na casa de décimos de 1% de um núcleo.

### Armadilhas de posicionamento na taskbar

Coisas que custaram uma rodada de depuração e valem lembrar antes de mexer em
`TaskbarPositionHelper`:

- **Não meça a janela com `GetWindowRect` durante `SizeChanged`.** Nesse momento o
  HWND ainda carrega o tamanho antigo, então o widget é ancorado pela medida
  anterior e desliza alguns pixels a cada pessoa que entra na call. Meça com
  `ActualWidth`/`ActualHeight` e converta por `TransformToDevice`.
- **`MainWindowHandle` é `IntPtr.Zero` para este processo.** É consequência
  esperada do `WS_EX_TOOLWINDOW`: a janela deixa de contar como principal. Para
  inspecioná-la de fora, enumere as janelas pelo PID.
- **Outros widgets podem estar *dentro* da taskbar, invisíveis para `EnumWindows`.**
  O FluentFlyout usa `SetParent` para virar filho de `Shell_TrayWnd`, com uma janela
  da largura da barra inteira recortada por `SetWindowRgn`. O retângulo dessa janela
  (1920px) não diz nada; só `GetWindowRgnBox` revela onde o conteúdo está — e ele
  muda de largura com o nome da música. Por isso o widget varre as janelas topmost
  **e** as filhas da taskbar que não pertencem ao explorer, lendo a região de cada
  uma, e reavalia a cada 2 s enquanto está visível.
- **A barra secundária não tem bandeja nem relógio como janela.** Não existe
  `TrayNotifyWnd` em `Shell_SecondaryTrayWnd`, e `SHAppBarMessage` só descreve a
  barra principal. Relógio e botão de notificações são elementos XAML sem HWND;
  a única forma de localizá-los é UI Automation (`AutomationId = "SystemTrayIcon"`,
  classe `SystemTray.OmniButtonLeft` para o relógio). Essa consulta é uma chamada COM
  entre processos que também acorda o explorer, então roda em segundo plano, com
  cache (`TaskbarClockLocator`), e só quando pedida: ao aparecer, quando a barra muda
  e na virada de cada minuto. Enquanto a primeira resposta não chega, o widget
  reserva 120px à direita para não aparecer por cima da data.
- **Esconder o widget é zerar a opacidade, não chamar `Hide()`.** Com
  `AllowsTransparency`, pixel totalmente transparente já deixa o clique passar para a
  barra, e evita-se um `Show()` que pode reativar a janela e tirar o foco do jogo.

### Verificação da posição

Sem depender do que está na tela, dá para conferir a ancoragem pela API do Windows.
Na barra secundária: o `GetWindowRect` do widget deve terminar 8px antes do
`BoundingRectangle` do relógio (via UI Automation). Na principal: 8px antes do
`TrayNotifyWnd` ou do widget vizinho. Nos dois casos a janela deve caber inteira
dentro do retângulo da barra.
