using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ParticipantViewModel(string userId, bool isSelf) : ObservableBase
{
    private ImageSource? _avatar;
    private string _displayName = string.Empty;
    private bool _isDeafened;
    private bool _isSilenced;
    private bool _isSpeaking;

    public string UserId { get; } = userId;

    public bool IsSelf { get; } = isSelf;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value)) Raise(nameof(Initial));
        }
    }

    /// <summary>
    /// Fallback enquanto o avatar carrega (ou se ele falhar).
    ///
    /// Primeiro elemento de texto, nao primeiro char: apelidos que comecam com emoji
    /// ("🎮 Fulano") teriam o par substituto cortado ao meio e virariam um glifo quebrado.
    /// </summary>
    public string Initial => string.IsNullOrEmpty(DisplayName)
        ? "?"
        : DisplayName[..System.Globalization.StringInfo.GetNextTextElementLength(DisplayName)].ToUpperInvariant();

    public ImageSource? Avatar
    {
        get => _avatar;
        set => Set(ref _avatar, value);
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set => Set(ref _isSpeaking, value);
    }

    public bool IsSilenced
    {
        get => _isSilenced;
        set => Set(ref _isSilenced, value);
    }

    /// <summary>Ensurdecido (implica silenciado): o selo mostra o fone cortado em vez do microfone.</summary>
    public bool IsDeafened
    {
        get => _isDeafened;
        set => Set(ref _isDeafened, value);
    }

    public string AvatarUrl { get; set; } = string.Empty;
}

public sealed class VoiceWidgetViewModel : ObservableBase
{
    private double _avatarDiameter = AvatarDiameterFor(AvatarSizeOption.Medium);
    private bool _isInCall;
    private bool _selfDeafened;
    private bool _selfMuted;
    private string _statusText = "Conectando...";

    public ObservableCollection<ParticipantViewModel> Participants { get; } = [];

    // ---- dimensoes, todas derivadas do diametro do avatar ------------------
    //
    // A barra tem 48px (100%). O limite e o tamanho grande: anel de 38px mais
    // 3px de respiro em cima e embaixo ainda cabe sem encostar nas bordas.

    public double AvatarDiameter
    {
        get => _avatarDiameter;
        private set
        {
            if (!Set(ref _avatarDiameter, value)) return;

            Raise(nameof(RingDiameter));
            Raise(nameof(BadgeDiameter));
            Raise(nameof(InitialFontSize));
            Raise(nameof(PillCornerRadius));
        }
    }

    /// <summary>Anel de fala: 4px de folga em volta do avatar.</summary>
    public double RingDiameter => AvatarDiameter + 8;

    public double BadgeDiameter => Math.Round(AvatarDiameter * 0.54);

    public double InitialFontSize => Math.Round(AvatarDiameter * 0.46);

    /// <summary>Metade da altura da pilula (anel + 3px de padding em cima e embaixo).</summary>
    public System.Windows.CornerRadius PillCornerRadius => new((RingDiameter + 6) / 2);

    public void ApplyAvatarSize(AvatarSizeOption size) => AvatarDiameter = AvatarDiameterFor(size);

    private static double AvatarDiameterFor(AvatarSizeOption size) => size switch
    {
        AvatarSizeOption.Small => 20,
        AvatarSizeOption.Large => 30,
        _ => 26,
    };

    public bool IsInCall
    {
        get => _isInCall;
        set => Set(ref _isInCall, value);
    }

    public bool SelfMuted
    {
        get => _selfMuted;
        set
        {
            if (Set(ref _selfMuted, value)) RaiseSelfDerived();
        }
    }

    public bool SelfDeafened
    {
        get => _selfDeafened;
        set
        {
            if (Set(ref _selfDeafened, value)) RaiseSelfDerived();
        }
    }

    /// <summary>Ensurdecido implica mudo: os dois pintam o icone de vermelho.</summary>
    public bool SelfSilenced => SelfMuted || SelfDeafened;

    public string SelfMicTooltip => SelfDeafened
        ? "Voce esta ensurdecido"
        : SelfMuted
            ? "Seu microfone esta mutado"
            : "Seu microfone esta aberto";

    private void RaiseSelfDerived()
    {
        Raise(nameof(SelfSilenced));
        Raise(nameof(SelfMicTooltip));
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>
    /// Reconcilia a lista no lugar em vez de recria-la.
    ///
    /// ParticipantsChanged dispara a cada VOICE_STATE_UPDATE (alguem mutou, mudou
    /// de volume...). Recriar os itens perderia o IsSpeaking de quem esta falando
    /// naquele instante e o anel verde piscaria a cada evento alheio.
    /// </summary>
    public void Sync(IReadOnlyList<VoiceParticipant> incoming, string? selfUserId)
    {
        var seen = new HashSet<string>(incoming.Count);

        foreach (var p in incoming)
        {
            seen.Add(p.UserId);

            var existing = Participants.FirstOrDefault(v => v.UserId == p.UserId);
            if (existing is null)
            {
                existing = new ParticipantViewModel(p.UserId, p.UserId == selfUserId);
                Participants.Add(existing);
            }

            existing.DisplayName = p.DisplayName;
            existing.IsSilenced = p.IsSilenced;
            existing.IsDeafened = p.IsDeafened;

            if (existing.AvatarUrl != p.AvatarUrl)
            {
                existing.AvatarUrl = p.AvatarUrl;
                LoadAvatar(existing, p.AvatarUrl);
            }
        }

        for (var i = Participants.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Participants[i].UserId)) Participants.RemoveAt(i);
        }
    }

    public void SetSpeaking(string userId, bool speaking)
    {
        var participant = Participants.FirstOrDefault(v => v.UserId == userId);
        if (participant is not null) participant.IsSpeaking = speaking;
    }

    private static void LoadAvatar(ParticipantViewModel target, string url)
    {
        // Caso comum (quem ja apareceu nesta sessao): atribui direto, sem trocar de thread.
        if (AvatarCache.TryGetCached(url, out var cached))
        {
            target.Avatar = cached;
            return;
        }

        _ = Task.Run(async () =>
        {
            var image = await AvatarCache.GetAsync(url);
            if (image is null) return;

            // O bitmap esta congelado, mas a propriedade e observada pela UI.
            // BeginInvoke: a thread de fundo nao precisa esperar a UI.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (target.AvatarUrl == url) target.Avatar = image;
            });
        });
    }
}
