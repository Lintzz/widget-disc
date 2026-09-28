using System.Diagnostics;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Fecha o Discord de desktop, como o "Sair do Discord" do icone na bandeja. O RPC
/// nao tem comando para isso, entao os processos sao encerrados direto.
/// </summary>
internal static class DiscordProcess
{
    /// <summary>Estavel, PTB e Canary: o widget conversa com qualquer um dos tres.</summary>
    private static readonly string[] Names = ["Discord", "DiscordPTB", "DiscordCanary"];

    public static bool IsRunning() => Names.Any(name =>
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    });

    /// <summary>Encerra todos os processos do Discord. Retorna quantos foram encerrados.</summary>
    public static int Quit()
    {
        var killed = 0;

        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        // A arvore inteira: os processos filhos (GPU, audio) morrem junto
                        // com o principal, sem depender da ordem da lista.
                        process.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch (Exception ex)
                    {
                        // Ja encerrado pela arvore de um irmao, ou sem permissao.
                        FileLog.Write($"fechar Discord ({process.Id}): {ex.Message}");
                    }
                }
            }
        }

        return killed;
    }
}
