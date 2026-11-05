using System.IO;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using DiscordVoiceWidget.Rpc;
using Microsoft.Win32;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Arquivo XML com tudo o que da para levar de um PC para outro: as preferencias, o
/// "iniciar com o Windows" e as credenciais do app do Discord.
///
/// O token OAuth fica de fora: o DPAPI o amarra a esta conta nesta maquina, entao ele nao
/// abriria depois de formatar. Com as credenciais de volta, o Discord so pede para
/// autorizar de novo.
///
/// O Client Secret vai em texto puro, porque o arquivo precisa abrir em outro PC, sem nada
/// que venha deste. As telas que exportam avisam disso.
/// </summary>
public sealed class SettingsBackup(DiscordCredentials? credentials, WidgetSettings settings, bool? startWithWindows)
{
    private const string RootName = "DiscordVoiceWidgetBackup";
    private const string FileFilter = "Configurações do widget (*.xml)|*.xml";

    /// <summary>Nulo se o arquivo nao trouxer credenciais preenchidas.</summary>
    public DiscordCredentials? Credentials { get; } = credentials is { IsFilled: true } ? credentials : null;

    public WidgetSettings Settings { get; } = settings;

    /// <summary>Nulo se o arquivo nao disser: fica como esta.</summary>
    public bool? StartWithWindows { get; } = startWithWindows;

    public static string? PickExportPath(Window? owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar configurações",
            Filter = FileFilter,
            FileName = "DiscordVoiceWidget-configuracoes.xml",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public static string? PickImportPath(Window? owner)
    {
        var dialog = new OpenFileDialog { Title = "Importar configurações", Filter = FileFilter };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public void Write(string path)
    {
        var placement = Settings.OverlayPlacement;

        var root = new XElement(RootName, new XAttribute("version", 1));
        if (Credentials is { } discord)
        {
            root.Add(new XElement("Discord",
                new XElement("ClientId", discord.ClientId),
                new XElement("ClientSecret", discord.ClientSecret),
                new XElement("RedirectUri", discord.RedirectUri)));
        }

        var settingsElement = new XElement("Settings",
            new XElement(nameof(WidgetSettings.Monitor), Settings.Monitor.ToString()),
            new XElement(nameof(WidgetSettings.HideWhenNotInCall), Settings.HideWhenNotInCall),
            new XElement(nameof(WidgetSettings.AvatarSize), Settings.AvatarSize.ToString()),
            new XElement(nameof(WidgetSettings.SpeakingReleaseMs), Settings.SpeakingReleaseMs),
            new XElement(nameof(WidgetSettings.OverlayEnabled), Settings.OverlayEnabled),
            new XElement(nameof(WidgetSettings.OverlayPlacement),
                new XElement(nameof(OverlayPlacement.Monitor), placement.Monitor),
                new XElement(nameof(OverlayPlacement.X), placement.X),
                new XElement(nameof(OverlayPlacement.Y), placement.Y)),
            new XElement(nameof(WidgetSettings.ToggleOverlayHotkey), Settings.ToggleOverlayHotkey),
            new XElement(nameof(WidgetSettings.MoveOverlayHotkey), Settings.MoveOverlayHotkey),
            new XElement(nameof(WidgetSettings.StreamLiveEnabled), Settings.StreamLiveEnabled));
        if (StartWithWindows is { } startup) settingsElement.Add(new XElement(nameof(StartWithWindows), startup));
        root.Add(settingsElement);

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(" Contem o Client Secret do seu app do Discord: guarde em lugar seguro e nao compartilhe. "),
            root);

        // Grava em arquivo temporario e troca: uma queda no meio nao estraga o backup anterior.
        var temp = path + ".tmp";
        document.Save(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Campo que faltar no arquivo fica no padrao, para um backup antigo continuar abrindo.
    /// </summary>
    /// <exception cref="InvalidDataException">O arquivo nao e um backup valido do widget.</exception>
    public static SettingsBackup Read(string path)
    {
        try
        {
            XDocument document;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                document = XDocument.Load(reader);
            }

            var root = document.Root;
            if (root?.Name != RootName)
            {
                throw new InvalidDataException("Este arquivo não é um backup do Discord Voice Widget.");
            }

            var discord = root.Element("Discord");
            var credentials = discord is null
                ? null
                : new DiscordCredentials(
                    ((string?)discord.Element("ClientId"))?.Trim() ?? string.Empty,
                    ((string?)discord.Element("ClientSecret"))?.Trim() ?? string.Empty,
                    NonBlank((string?)discord.Element("RedirectUri")) ?? DiscordCredentials.DefaultRedirectUri);

            var element = root.Element("Settings");
            var settings = new WidgetSettings();
            if (element is null) return new SettingsBackup(credentials, settings, null);

            settings.Monitor = ReadEnum(element, nameof(WidgetSettings.Monitor), settings.Monitor);
            settings.HideWhenNotInCall = (bool?)element.Element(nameof(WidgetSettings.HideWhenNotInCall)) ?? settings.HideWhenNotInCall;
            settings.AvatarSize = ReadEnum(element, nameof(WidgetSettings.AvatarSize), settings.AvatarSize);
            settings.SpeakingReleaseMs = (int?)element.Element(nameof(WidgetSettings.SpeakingReleaseMs)) ?? settings.SpeakingReleaseMs;
            settings.OverlayEnabled = (bool?)element.Element(nameof(WidgetSettings.OverlayEnabled)) ?? settings.OverlayEnabled;
            settings.ToggleOverlayHotkey = (string?)element.Element(nameof(WidgetSettings.ToggleOverlayHotkey)) ?? settings.ToggleOverlayHotkey;
            settings.MoveOverlayHotkey = (string?)element.Element(nameof(WidgetSettings.MoveOverlayHotkey)) ?? settings.MoveOverlayHotkey;
            settings.StreamLiveEnabled = (bool?)element.Element(nameof(WidgetSettings.StreamLiveEnabled)) ?? settings.StreamLiveEnabled;

            if (element.Element(nameof(WidgetSettings.OverlayPlacement)) is { } placement)
            {
                var overlay = settings.OverlayPlacement;
                overlay.Monitor = (string?)placement.Element(nameof(OverlayPlacement.Monitor)) ?? overlay.Monitor;
                overlay.X = (double?)placement.Element(nameof(OverlayPlacement.X)) ?? overlay.X;
                overlay.Y = (double?)placement.Element(nameof(OverlayPlacement.Y)) ?? overlay.Y;
            }

            return new SettingsBackup(
                credentials,
                settings.Normalized(),
                (bool?)element.Element(nameof(StartWithWindows)));
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"O arquivo está danificado: {ex.Message}", ex);
        }
    }

    /// <summary>Liga ou desliga a inicializacao com o Windows, se o arquivo disser como era.</summary>
    public void ApplyStartup()
    {
        if (StartWithWindows is not { } wanted || wanted == StartupRegistration.IsEnabled) return;

        try
        {
            StartupRegistration.SetEnabled(wanted);
        }
        catch (Exception ex)
        {
            // Nao vale derrubar o resto da importacao: a opcao continua na tela para tentar de novo.
            FileLog.Write($"falha ao aplicar inicializacao do backup: {ex.Message}");
        }
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T ReadEnum<T>(XElement parent, string name, T fallback) where T : struct, Enum
        => Enum.TryParse<T>((string?)parent.Element(name), out var value) && Enum.IsDefined(value) ? value : fallback;
}
