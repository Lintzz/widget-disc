using Microsoft.Win32;

namespace DiscordVoiceWidget.App;

/// <summary>
/// "Iniciar com o Windows" pela chave Run do usuario atual. Nao exige
/// administrador e aparece em Gerenciador de Tarefas &gt; Aplicativos de inicializacao,
/// onde o usuario tambem pode desligar.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiscordVoiceWidget";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command
                   && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Caminho do executavel atual. Se o widget for movido de pasta, a entrada antiga
    /// para de valer e <see cref="IsEnabled"/> passa a dizer false - o que e o certo.
    /// </summary>
    public static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

        if (enabled && ExecutablePath.Length > 0)
        {
            key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
