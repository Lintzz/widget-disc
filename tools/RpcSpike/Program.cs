using DiscordVoiceWidget.Rpc;

// ---------------------------------------------------------------------------
// Harness da biblioteca DiscordVoiceWidget.Rpc.
//
// Consome exatamente a mesma API que a UI da Fase 3 vai consumir, so que
// imprimindo no console em vez de desenhar avatares. Serve para validar a
// camada de voz sem WPF no caminho.
// ---------------------------------------------------------------------------

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var credentials = DiscordCredentials.LoadOrBootstrap();
if (!credentials.IsFilled)
{
    Write(ConsoleColor.Red, $"Preencha clientId e clientSecret em {AppPaths.Config}");
    Console.WriteLine();
    Console.WriteLine("  1. https://discord.com/developers/applications -> New Application");
    Console.WriteLine("  2. Application ID              -> clientId");
    Console.WriteLine("  3. OAuth2 -> Client Secret     -> clientSecret");
    Console.WriteLine("  4. OAuth2 -> Redirects -> adicione  https://localhost  (sem barra final)");
    return 1;
}

// A UI vai manter esse mapa em um ViewModel; aqui ele so serve para dar nome
// aos eventos de fala, que trazem apenas o user_id.
var names = new Dictionary<string, string>();

await using var session = new VoiceSessionService(credentials);

session.Log += m => Write(ConsoleColor.DarkGray, $"  {m}");

session.StateChanged += (state, detail) =>
{
    var color = state switch
    {
        VoiceConnectionState.InCall => ConsoleColor.Green,
        VoiceConnectionState.Connected => ConsoleColor.Cyan,
        VoiceConnectionState.NeedsAuthorization => ConsoleColor.Yellow,
        VoiceConnectionState.Disconnected => ConsoleColor.Red,
        _ => ConsoleColor.DarkGray,
    };

    Write(color, $"[estado] {state}{(detail is null ? "" : $" - {detail}")}");

    if (state == VoiceConnectionState.NeedsAuthorization)
    {
        Write(ConsoleColor.Yellow, "  >> Um modal vai abrir no Discord. Clique em Authorize. <<");
    }
};

session.ParticipantsChanged += participants =>
{
    names.Clear();
    foreach (var p in participants) names[p.UserId] = p.DisplayName;

    if (participants.Count == 0)
    {
        Write(ConsoleColor.DarkGray, "  (fora de call)");
        return;
    }

    Console.WriteLine($"  participantes ({participants.Count}):");
    foreach (var p in participants)
    {
        var self = p.UserId == session.SelfUserId ? " <- voce" : string.Empty;
        var flags = p.IsSilenced ? " [silenciado]" : string.Empty;
        Console.WriteLine($"    - {p.DisplayName,-24}{flags}{self}");
    }
};

session.SpeakingChanged += (userId, speaking) =>
{
    var who = names.TryGetValue(userId, out var name) ? name : userId;
    Write(
        speaking ? ConsoleColor.Green : ConsoleColor.DarkGray,
        $"[{DateTime.Now:HH:mm:ss.fff}] {(speaking ? "FALANDO  " : "  silencio")} {who}");
};

session.SelfVoiceSettingsChanged += (muted, deafened) =>
    Write(
        muted || deafened ? ConsoleColor.Red : ConsoleColor.Green,
        $"[{DateTime.Now:HH:mm:ss}] MEU MIC  {(deafened ? "ensurdecido" : muted ? "mutado" : "aberto")}");

Write(ConsoleColor.White, "Iniciando (Ctrl+C para sair)");
await session.StartAsync(cts.Token);

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Encerrado.");
}

return 0;

static void Write(ConsoleColor color, string text)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}
