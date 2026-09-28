using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordVoiceWidget.Rpc;

public enum VoiceConnectionState
{
    /// <summary>Discord fechado ou pipe indisponivel.</summary>
    Disconnected,

    /// <summary>Tentando abrir o pipe.</summary>
    Connecting,

    /// <summary>Conectado, mas o usuario precisa aceitar o modal de autorizacao.</summary>
    NeedsAuthorization,

    /// <summary>Autenticado, porem fora de qualquer canal de voz.</summary>
    Connected,

    /// <summary>Dentro de um canal de voz - o widget deve aparecer.</summary>
    InCall,
}

/// <summary>Uma pessoa no canal de voz.</summary>
public sealed record VoiceParticipant(
    string UserId,
    string DisplayName,
    string AvatarUrl,
    bool SelfMuted,
    bool SelfDeafened,
    bool ServerMuted,
    bool ServerDeafened)
{
    public bool IsDeafened => SelfDeafened || ServerDeafened;

    public bool IsSilenced => SelfMuted || ServerMuted || IsDeafened;

    /// <summary>
    /// Le tanto os itens de GET_SELECTED_VOICE_CHANNEL.voice_states[] quanto o
    /// payload dos eventos VOICE_STATE_CREATE / UPDATE / DELETE - a forma e a mesma.
    /// </summary>
    public static VoiceParticipant? From(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return null;
        if (!state.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object) return null;

        var userId = user.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (userId is null) return null;

        var display = FirstNonBlank(Str(state, "nick"), Str(user, "global_name"), Str(user, "username")) ?? userId;
        var voice = state.TryGetProperty("voice_state", out var v) ? v : default;

        return new VoiceParticipant(
            userId,
            display,
            AvatarUrlFor(userId, Str(user, "avatar")),
            Bool(voice, "self_mute"),
            Bool(voice, "self_deaf"),
            Bool(voice, "mute"),
            Bool(voice, "deaf"));
    }

    public static string AvatarUrlFor(string userId, string? avatarHash, int size = 64)
    {
        if (!string.IsNullOrEmpty(avatarHash))
        {
            return $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png?size={size}";
        }

        // Avatar padrao do sistema novo de usernames: (id >> 22) % 6.
        var index = ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? (id >> 22) % 6
            : 0;
        return $"https://cdn.discordapp.com/embed/avatars/{index}.png";
    }

    private static string? Str(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var el)
           && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool Bool(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var el)
           && el.ValueKind == JsonValueKind.True;

    // Sem params/LINQ: roda a cada VOICE_STATE_UPDATE, e o array e o delegate seriam lixo.
    private static string? FirstNonBlank(string? a, string? b, string? c)
        => !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : !string.IsNullOrWhiteSpace(c) ? c : null;
}

/// <summary>Credenciais do app, lidas de fora do repositorio.</summary>
public sealed record DiscordCredentials(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret,
    [property: JsonPropertyName("redirectUri")] string RedirectUri)
{
    public const string Placeholder = "COLE_AQUI";

    public bool IsFilled =>
        !string.IsNullOrWhiteSpace(ClientId) && ClientId != Placeholder &&
        !string.IsNullOrWhiteSpace(ClientSecret) && ClientSecret != Placeholder;

    public const string DefaultRedirectUri = "https://localhost";

    public static DiscordCredentials LoadOrBootstrap()
    {
        if (!File.Exists(AppPaths.Config))
        {
            new DiscordCredentials(Placeholder, Placeholder, DefaultRedirectUri).Save();
        }

        return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.Config), RpcJsonContext.Default.DiscordCredentials)
               ?? throw new InvalidOperationException($"{AppPaths.Config} esta vazio ou invalido.");
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Dir);
        File.WriteAllText(AppPaths.Config, JsonSerializer.Serialize(this, RpcJsonContext.Default.DiscordCredentials));
    }
}

public sealed record OAuthToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Renovamos com 1 dia de folga - o token do Discord vale 7 dias.</summary>
    public bool NeedsRefresh => DateTimeOffset.UtcNow > ExpiresAt - TimeSpan.FromDays(1);
}

public static class AppPaths
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiscordVoiceWidget");

    public static string Config => Path.Combine(Dir, "config.json");

    public static string Token => Path.Combine(Dir, "token.dat");

    public static string LocalDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscordVoiceWidget");

    public static string AvatarCache => Path.Combine(LocalDir, "avatars");
}
