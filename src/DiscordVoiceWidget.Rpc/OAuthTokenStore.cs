using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiscordVoiceWidget.Rpc;

/// <summary>
/// Guarda o token OAuth protegido por DPAPI, amarrado a conta do Windows:
/// o arquivo so e legivel pelo mesmo usuario nesta mesma maquina.
/// </summary>
public static class OAuthTokenStore
{
    // Entropia adicional: um arquivo token.dat copiado para outro app do mesmo
    // usuario ainda falha ao descriptografar.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DiscordVoiceWidget.v1");

    public static OAuthToken? Load()
    {
        if (!File.Exists(AppPaths.Token)) return null;

        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(AppPaths.Token),
                Entropy,
                DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize(plain, RpcJsonContext.Default.OAuthToken);
        }
        catch
        {
            // Token corrompido, de outra maquina ou de outro usuario: trate como ausente.
            return null;
        }
    }

    public static void Save(OAuthToken token)
    {
        Directory.CreateDirectory(AppPaths.Dir);

        var cipher = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(token, RpcJsonContext.Default.OAuthToken),
            Entropy,
            DataProtectionScope.CurrentUser);

        File.WriteAllBytes(AppPaths.Token, cipher);
    }

    public static void Clear()
    {
        if (File.Exists(AppPaths.Token)) File.Delete(AppPaths.Token);
    }
}

public static class DiscordOAuth
{
    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";

    private static readonly HttpClient Http = new();

    public static async Task<OAuthToken> ExchangeCodeAsync(
        DiscordCredentials credentials,
        string code,
        CancellationToken ct = default)
        => await PostAsync(
            credentials,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                // Obrigatorio aqui, e proibido no comando AUTHORIZE do RPC.
                ["redirect_uri"] = credentials.RedirectUri,
            },
            ct);

    public static async Task<OAuthToken> RefreshAsync(
        DiscordCredentials credentials,
        string refreshToken,
        CancellationToken ct = default)
        => await PostAsync(
            credentials,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            },
            ct);

    private static async Task<OAuthToken> PostAsync(
        DiscordCredentials credentials,
        Dictionary<string, string> fields,
        CancellationToken ct)
    {
        fields["client_id"] = credentials.ClientId;
        fields["client_secret"] = credentials.ClientSecret;

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(fields), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // 400/401 aqui e credencial errada (secret, client id ou redirect), nao falha
        // passageira: tentar de novo nao resolve, o usuario precisa corrigir.
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
        {
            throw new DiscordCredentialsRejectedException(
                $"Endpoint de token respondeu {(int)response.StatusCode}: {body}. " +
                "Confira clientSecret e se o redirectUri bate exatamente com o cadastrado no portal.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DiscordRpcException(
                $"Endpoint de token respondeu {(int)response.StatusCode}: {body}. " +
                "Confira clientSecret e se o redirectUri bate exatamente com o cadastrado no portal.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
                          ?? throw new DiscordRpcException($"Resposta sem access_token: {body}");
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 604800;

        return new OAuthToken(accessToken, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}
