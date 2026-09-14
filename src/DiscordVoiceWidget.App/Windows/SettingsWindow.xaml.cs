using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Preferencias aplicadas na hora, sem botao Salvar: o widget esta na barra ao
/// lado, e ver o efeito enquanto ajusta e o jeito mais facil de escolher.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string RecordingPrompt = "Aperte a combinação...";

    /// <summary>Largura ao abrir, se a tela comportar: cabe em duas colunas.</summary>
    private const double PreferredWidth = 860;

    /// <summary>A partir desta largura as secoes ficam lado a lado.</summary>
    private const double TwoColumnMinWidth = 760;

    /// <summary>Espaco entre as duas colunas.</summary>
    private const double ColumnGap = 32;

    private readonly WidgetSettings _settings;

    // Evita tratar como edicao do usuario a carga inicial dos controles.
    private bool _loading = true;
    private bool? _twoColumns;

    public SettingsWindow(WidgetSettings current)
    {
        InitializeComponent();
        ConfigureInitialSize();

        _settings = current.Clone();

        SelectByTag(MonitorCombo, _settings.Monitor.ToString());
        SelectByTag(AvatarSizeCombo, _settings.AvatarSize.ToString());
        HideNotInCallCheck.IsChecked = _settings.HideWhenNotInCall;
        ReleaseSlider.Value = _settings.SpeakingReleaseMs;
        ReleaseLabel.Text = $"{_settings.SpeakingReleaseMs} ms";

        OverlayCheck.IsChecked = _settings.OverlayEnabled;
        ToggleHotkeyBox.Text = _settings.ToggleOverlayHotkey;
        MoveHotkeyBox.Text = _settings.MoveOverlayHotkey;

        StartupCheck.IsChecked = StartupRegistration.IsEnabled;
        UpdateStartupHint();

        _loading = false;
    }

    /// <summary>Disparado a cada alteracao, ja com o valor novo.</summary>
    public event Action<WidgetSettings>? SettingsChanged;

    // -----------------------------------------------------------------------
    // Layout responsivo
    // -----------------------------------------------------------------------

    /// <summary>
    /// Abre largo o bastante para duas colunas (se a tela deixar) e com a altura do
    /// conteudo, mas nunca maior que 90% da area util: numa tela baixa, o conteudo rola
    /// e o rodape com "Fechar" continua a vista.
    /// </summary>
    private void ConfigureInitialSize()
    {
        var workArea = SystemParameters.WorkArea;

        Width = Math.Max(MinWidth, Math.Min(PreferredWidth, workArea.Width * 0.9));
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.9);
        ApplyColumns(Width);

        // Altura do conteudo so na abertura; depois do primeiro desenho a janela passa a
        // ter tamanho livre, para o usuario redimensionar.
        SizeToContent = SizeToContent.Height;
        ContentRendered += (_, _) => SizeToContent = SizeToContent.Manual;

        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) ApplyColumns(e.NewSize.Width);
        };
    }

    private void ApplyColumns(double windowWidth)
    {
        var twoColumns = windowWidth >= TwoColumnMinWidth;
        if (_twoColumns == twoColumns) return;
        _twoColumns = twoColumns;

        var second = ColumnsGrid.ColumnDefinitions[1];

        if (twoColumns)
        {
            second.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(RightColumn, 0);
            Grid.SetColumn(RightColumn, 1);
            RightColumn.Margin = new Thickness(ColumnGap, 0, 0, 0);

            // Primeira secao da coluna: alinha o titulo com o "Posição" da esquerda.
            OverlayHeader.Style = (Style)FindResource("FirstSectionHeader");
        }
        else
        {
            second.Width = new GridLength(0);
            Grid.SetRow(RightColumn, 1);
            Grid.SetColumn(RightColumn, 0);
            RightColumn.Margin = new Thickness(0);
            OverlayHeader.Style = (Style)FindResource("SectionHeader");
        }
    }

    /// <summary>
    /// Enquanto um campo de atalho grava, os atalhos globais precisam ficar desligados:
    /// senao o proprio atalho seria capturado pelo Windows antes de chegar ao campo.
    /// </summary>
    public event Action<bool>? HotkeyRecordingChanged;

    public event Action? MoveOverlayRequested;

    public event Action? CredentialsChangeRequested;

    /// <summary>
    /// Reflete mudancas feitas fora desta tela (atalho de teclado, menu da bandeja)
    /// enquanto ela esta aberta, para que a proxima edicao aqui nao as desfaca.
    /// </summary>
    public void ReflectExternalChange(WidgetSettings current)
    {
        _loading = true;

        _settings.OverlayEnabled = current.OverlayEnabled;
        _settings.OverlayPlacement = current.Clone().OverlayPlacement;
        OverlayCheck.IsChecked = current.OverlayEnabled;

        _loading = false;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (TagOf(MonitorCombo) is { } monitor) _settings.Monitor = Enum.Parse<TaskbarMonitorPreference>(monitor);
        if (TagOf(AvatarSizeCombo) is { } size) _settings.AvatarSize = Enum.Parse<AvatarSizeOption>(size);
        _settings.HideWhenNotInCall = HideNotInCallCheck.IsChecked == true;
        _settings.SpeakingReleaseMs = (int)ReleaseSlider.Value;

        _settings.OverlayEnabled = OverlayCheck.IsChecked == true;

        SettingsChanged?.Invoke(_settings.Clone());
    }

    private void OnReleaseChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ReleaseLabel is not null) ReleaseLabel.Text = $"{(int)e.NewValue} ms";
        OnChanged(sender, e);
    }

    // -----------------------------------------------------------------------
    // Gravacao de atalhos
    // -----------------------------------------------------------------------

    private void OnHotkeyFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ((TextBox)sender).Text = RecordingPrompt;
        HotkeyRecordingChanged?.Invoke(true);
    }

    private void OnHotkeyBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Saiu sem gravar nada: volta a mostrar o atalho atual.
        var box = (TextBox)sender;
        box.Text = box == ToggleHotkeyBox ? _settings.ToggleOverlayHotkey : _settings.MoveOverlayHotkey;
        HotkeyRecordingChanged?.Invoke(false);
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                Keyboard.ClearFocus();
                return;

            case Key.Back or Key.Delete:
                StoreHotkey(box, string.Empty);
                return;
        }

        var modifiers = Keyboard.Modifiers;
        if (!HotkeyBinding.IsAcceptable(modifiers, key))
        {
            // So modificadores ate agora: mostra o que esta sendo montado.
            box.Text = modifiers == ModifierKeys.None ? RecordingPrompt : string.Join("+", Describe(modifiers)) + "+...";
            return;
        }

        StoreHotkey(box, new HotkeyBinding(modifiers, key).ToString());
    }

    private void StoreHotkey(TextBox box, string value)
    {
        if (box == ToggleHotkeyBox) _settings.ToggleOverlayHotkey = value;
        else _settings.MoveOverlayHotkey = value;

        // Sai do campo: grava, volta a registrar os atalhos e mostra o resultado.
        Keyboard.ClearFocus();
        SettingsChanged?.Invoke(_settings.Clone());
    }

    private static IEnumerable<string> Describe(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    private void OnMoveOverlayClick(object sender, RoutedEventArgs e) => MoveOverlayRequested?.Invoke();

    // -----------------------------------------------------------------------
    // Sistema
    // -----------------------------------------------------------------------

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        try
        {
            StartupRegistration.SetEnabled(StartupCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"falha ao alterar inicializacao: {ex.Message}");
            MessageBox.Show(this, $"Não foi possível alterar a inicialização:\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);

            _loading = true;
            StartupCheck.IsChecked = StartupRegistration.IsEnabled;
            _loading = false;
        }

        UpdateStartupHint();
    }

    private void UpdateStartupHint()
    {
        var path = StartupRegistration.ExecutablePath;

        // Rodando pelo dotnet run o executavel mora em bin\Debug, que some num
        // "dotnet clean". Avisar evita um autostart que para de funcionar sozinho.
        StartupHint.Text = path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            ? $"Atenção: executando a partir de uma pasta de build ({path}). Use o instalador antes de ativar."
            : $"Executável: {path}";
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenFolder(FileLog.Directory);

    private void OnOpenConfigClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Dir);

    private void OnChangeCredentialsClick(object sender, RoutedEventArgs e) => CredentialsChangeRequested?.Invoke();

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag)
                             ?? combo.Items[0];
    }

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
}
