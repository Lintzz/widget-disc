using System.IO;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Log diario em %LOCALAPPDATA%\DiscordVoiceWidget\logs, guardado por 7 dias.
///
/// O app nao tem console: sem isto, uma falha de autorizacao ou reconexao em
/// loop seria invisivel. Intencionalmente simples - uma linha por evento.
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string Directory { get; } = Path.Combine(AppPaths.LocalDir, "logs");

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
            var path = Path.Combine(Directory, $"widget-{DateTime.Now:yyyyMMdd}.log");

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Log nunca pode derrubar o widget (disco cheio, pasta sem permissao).
        }
    }

    public static void PruneOldFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "widget-*.log"))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > Retention) File.Delete(file);
            }
        }
        catch
        {
            // idem
        }
    }
}
