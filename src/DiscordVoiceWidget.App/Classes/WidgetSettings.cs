using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

public enum TaskbarMonitorPreference
{
    /// <summary>Barra do monitor secundario, ao lado do relogio. Recai na principal se nao houver.</summary>
    Secondary,

    /// <summary>Barra do monitor principal, ao lado da bandeja.</summary>
    Primary,

    /// <summary>
    /// Um widget em cada barra. Sem monitor secundario, so o da principal aparece.
    /// </summary>
    Both,
}

/// <summary>
/// Posicao do overlay: canto superior esquerdo como fracao (0..1) do monitor.
/// Fracao em vez de pixels para sobreviver a mudanca de resolucao.
/// </summary>
public sealed class OverlayPlacement
{
    /// <summary>Nome de dispositivo do monitor (\\.\DISPLAY1). Vazio = principal.</summary>
    public string Monitor { get; set; } = string.Empty;

    public double X { get; set; } = 0.012;

    public double Y { get; set; } = 0.12;
}

public enum AvatarSizeOption
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Preferencias do widget, em %APPDATA%\DiscordVoiceWidget\settings.json.
///
/// Fica separado do config.json de proposito: aquele guarda o client secret e o
/// usuario edita a mao uma vez; este muda pela tela de configuracoes. "Iniciar com
/// o Windows" nao mora aqui - a fonte da verdade e o registro (ver
/// <see cref="StartupRegistration"/>), senao os dois poderiam discordar.
/// </summary>
public sealed class WidgetSettings
{
    public const int MinSpeakingReleaseMs = 100;
    public const int MaxSpeakingReleaseMs = 1000;

    public static string FilePath => Path.Combine(AppPaths.Dir, "settings.json");

    public TaskbarMonitorPreference Monitor { get; set; } = TaskbarMonitorPreference.Secondary;

    public bool HideWhenNotInCall { get; set; } = true;

    public AvatarSizeOption AvatarSize { get; set; } = AvatarSizeOption.Medium;

    /// <summary>Quanto o anel verde fica aceso depois que a pessoa para de falar.</summary>
    public int SpeakingReleaseMs { get; set; } = 350;

    // ---- overlay nos jogos --------------------------------------------------

    /// <summary>
    /// Desligado por padrao. Ligado, o overlay aparece enquanto o widget estiver ativo
    /// (em call); nao ha deteccao automatica de jogo ou tela cheia - quem decide e o usuario.
    /// </summary>
    public bool OverlayEnabled { get; set; }

    public OverlayPlacement OverlayPlacement { get; set; } = new();

    /// <summary>Liga/desliga o overlay de qualquer lugar, inclusive dentro do jogo.</summary>
    public string ToggleOverlayHotkey { get; set; } = "Ctrl+Shift+O";

    /// <summary>Entra e sai do modo de posicionar o overlay com o mouse.</summary>
    public string MoveOverlayHotkey { get; set; } = "Ctrl+Shift+P";

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.WidgetSettings);
                if (loaded is not null) return loaded.Normalized();
            }
        }
        catch (Exception ex)
        {
            // Arquivo corrompido ou editado a mao com erro: segue com o padrao em vez
            // de impedir o widget de abrir.
            FileLog.Write($"settings.json invalido, usando padroes: {ex.Message}");
        }

        return new WidgetSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Dir);

        // Grava em arquivo temporario e troca: uma queda no meio da escrita nao
        // deixa um settings.json pela metade.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Normalized(), AppJsonContext.Default.WidgetSettings));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Copia independente, inclusive da posicao do overlay (objeto aninhado).</summary>
    public WidgetSettings Clone()
    {
        var copy = (WidgetSettings)MemberwiseClone();
        copy.OverlayPlacement = new OverlayPlacement
        {
            Monitor = OverlayPlacement.Monitor,
            X = OverlayPlacement.X,
            Y = OverlayPlacement.Y,
        };
        return copy;
    }

    private WidgetSettings Normalized()
    {
        SpeakingReleaseMs = Math.Clamp(SpeakingReleaseMs, MinSpeakingReleaseMs, MaxSpeakingReleaseMs);
        if (!Enum.IsDefined(Monitor)) Monitor = TaskbarMonitorPreference.Secondary;
        if (!Enum.IsDefined(AvatarSize)) AvatarSize = AvatarSizeOption.Medium;

        OverlayPlacement ??= new OverlayPlacement();
        OverlayPlacement.Monitor ??= string.Empty;
        OverlayPlacement.X = double.IsFinite(OverlayPlacement.X) ? Math.Clamp(OverlayPlacement.X, 0, 1) : 0.012;
        OverlayPlacement.Y = double.IsFinite(OverlayPlacement.Y) ? Math.Clamp(OverlayPlacement.Y, 0, 1) : 0.12;

        // Atalho ilegivel (settings.json editado a mao) volta ao padrao em vez de sumir.
        ToggleOverlayHotkey = NormalizeHotkey(ToggleOverlayHotkey, "Ctrl+Shift+O");
        MoveOverlayHotkey = NormalizeHotkey(MoveOverlayHotkey, "Ctrl+Shift+P");
        return this;
    }

    /// <summary>Texto vazio e valido (atalho desligado); texto invalido volta ao padrao.</summary>
    private static string NormalizeHotkey(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var parsed = HotkeyBinding.Parse(value);
        return parsed.IsEmpty ? fallback : parsed.ToString();
    }
}

/// <summary>
/// Serializacao gerada em tempo de compilacao (ver RpcJsonContext na biblioteca):
/// sem reflexao, que o app desliga de vez.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WidgetSettings))]
[JsonSerializable(typeof(OverlayPlacement))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
