using System.Diagnostics;
using System.Windows;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Pede o Application ID e o Client Secret do app do usuario no Developer Portal,
/// no lugar de editar o config.json na mao. Usada na primeira execucao e pelas
/// configuracoes, para trocar de app ou corrigir um secret errado.
/// </summary>
public partial class CredentialsWindow : Window
{
    private const string PortalUrl = "https://discord.com/developers/applications";

    private readonly string _redirectUri;

    public CredentialsWindow(DiscordCredentials? current, string? error = null)
    {
        InitializeComponent();

        _redirectUri = string.IsNullOrWhiteSpace(current?.RedirectUri)
            ? DiscordCredentials.DefaultRedirectUri
            : current.RedirectUri;
        RedirectBox.Text = _redirectUri;

        if (current is { IsFilled: true })
        {
            ClientIdBox.Text = current.ClientId;
            SecretBox.Password = current.ClientSecret;
        }

        // Depois de preencher os campos: preencher dispara OnInputChanged, que esconde o erro.
        ShowError(error);

        Loaded += (_, _) =>
        {
            if (error is null) ClientIdBox.Focus();
            else SecretBox.Focus();
        };
    }

    /// <summary>As credenciais digitadas, preenchido quando a janela fecha com Salvar.</summary>
    public DiscordCredentials? Result { get; private set; }

    private void OnOpenPortalClick(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(PortalUrl) { UseShellExecute = true });

    private void OnCopyRedirectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_redirectUri);
            CopyRedirectButton.Content = "Copiado";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Area de transferencia ocupada por outro processo: o campo e selecionavel.
            RedirectBox.Focus();
            RedirectBox.SelectAll();
        }
    }

    private void OnInputChanged(object sender, RoutedEventArgs e) => ShowError(null);

    private void ShowError(string? message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        var secret = SecretBox.Password.Trim();

        if (Validate(clientId, secret) is { } error)
        {
            ShowError(error);
            return;
        }

        Result = new DiscordCredentials(clientId, secret, _redirectUri);
        DialogResult = true;
    }

    /// <summary>
    /// Pega os enganos comuns antes de ir ao Discord, onde viram so um "400" no log:
    /// campo vazio, e colar a Public Key ou o proprio ID no lugar do secret.
    /// </summary>
    private static string? Validate(string clientId, string secret)
    {
        if (clientId.Length == 0) return "Cole o Application ID.";

        if (clientId.Length is < 17 or > 20 || !clientId.All(char.IsAsciiDigit))
        {
            return "O Application ID tem só números (17 a 20 dígitos). Ele fica em General Information.";
        }

        if (secret.Length == 0) return "Cole o Client Secret.";

        if (secret == clientId || secret.All(char.IsAsciiDigit))
        {
            return "Isso parece o Application ID. O Client Secret fica em OAuth2 → Reset Secret.";
        }

        if (secret.Length == 64 && secret.All(char.IsAsciiHexDigit))
        {
            return "Isso parece a Public Key. O Client Secret fica em OAuth2 → Reset Secret.";
        }

        if (secret.Any(char.IsWhiteSpace))
        {
            return "O Client Secret não tem espaços. Copie de novo em OAuth2 → Reset Secret.";
        }

        return null;
    }
}
