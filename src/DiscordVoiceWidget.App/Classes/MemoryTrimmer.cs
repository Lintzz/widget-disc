using System.Runtime;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Devolve ao sistema a memoria que o widget nao esta usando.
///
/// Um processo WPF pega bem mais memoria na inicializacao (fontes, dicionarios de
/// recursos, JIT) do que precisa depois, e o .NET segura as paginas por padrao. Para
/// um app que fica aberto o dia todo e passa a maior parte do tempo parado, faz sentido
/// devolver isso - e memoria fisica a mais para o jogo.
///
/// Custa uma coleta completa de alguns milissegundos e algumas faltas de pagina se a
/// memoria for usada de novo, por isso roda so em transicoes (inicializacao
/// concluida, saida de call, configuracoes fechadas) e no maximo a cada 2 minutos.
/// </summary>
internal static class MemoryTrimmer
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);

    private static long _lastTrimTicks = long.MinValue / 2;

    public static void Trim()
    {
        var now = Environment.TickCount64;
        if (now - _lastTrimTicks < (long)MinInterval.TotalMilliseconds) return;
        _lastTrimTicks = now;

        // Aggressive: alem de compactar, devolve ao sistema as regioes livres do heap
        // em vez de guarda-las para reuso.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // (-1, -1): descarta do working set as paginas residentes que nao estao em uso.
        // Elas vao para a lista de espera do Windows e voltam sob demanda.
        NativeMethods.SetProcessWorkingSetSizeEx(NativeMethods.GetCurrentProcess(), -1, -1, 0);

        FileLog.Write($"memoria devolvida ao sistema (heap gerenciado: {GC.GetTotalMemory(false) / 1024} KB)");
    }
}
