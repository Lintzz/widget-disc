using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Call de mentira para o modo demonstracao ("--demo"): participantes ficticios com
/// avatares gerados e uma sequencia fixa de quem fala. Serve para gravar os prints do
/// README sem conectar ao Discord e sem mostrar dados de ninguem.
/// </summary>
internal sealed class DemoCall : IDisposable
{
    private static readonly (string Id, string Name, Color From, Color To, bool Silenced)[] People =
    [
        ("self", "Você", Color.FromRgb(0xF7, 0x97, 0x1E), Color.FromRgb(0xE2, 0x4A, 0x6B), false),
        ("ana", "Ana", Color.FromRgb(0x2B, 0xC0, 0xA8), Color.FromRgb(0x1F, 0x6F, 0xD1), false),
        ("bruno", "Bruno", Color.FromRgb(0x9B, 0x6B, 0xF2), Color.FromRgb(0x4C, 0x3B, 0xC9), false),
        ("carla", "Carla", Color.FromRgb(0xF2, 0xC9, 0x4C), Color.FromRgb(0xE0, 0x7B, 0x28), true),
        ("davi", "Davi", Color.FromRgb(0x5F, 0xC8, 0x5A), Color.FromRgb(0x23, 0x8A, 0x5B), false),
    ];

    /// <summary>Quem fala em cada passo. Fixo, para os prints e o GIF sairem iguais sempre.</summary>
    private static readonly string[][] Script =
    [
        ["ana"], ["ana"], ["ana"], [], ["bruno"], ["bruno"], ["bruno", "ana"], [],
        ["self"], ["self"], ["self"], [], ["davi"], ["davi"], [],
    ];

    private readonly VoiceWidgetViewModel _viewModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private int _step;

    public DemoCall(VoiceWidgetViewModel viewModel, bool selfMuted, bool selfDeafened = false)
    {
        _viewModel = viewModel;
        _viewModel.SelfMuted = selfMuted;
        _viewModel.SelfDeafened = selfDeafened;

        foreach (var person in People)
        {
            _viewModel.Participants.Add(new ParticipantViewModel(person.Id, person.Id == "self")
            {
                DisplayName = person.Name,
                IsSilenced = person.Silenced || (person.Id == "self" && selfMuted),
                Avatar = RenderAvatar(person.Name, person.From, person.To),
            });
        }

        // Mutado, "voce" nao fala: o anel verde ficaria em contradicao com o icone vermelho.
        _timer.Tick += (_, _) => Advance(skipSelf: selfMuted);
    }

    public void Start()
    {
        Advance(skipSelf: _viewModel.SelfMuted);
        _timer.Start();
    }

    private void Advance(bool skipSelf)
    {
        var speaking = Script[_step++ % Script.Length];
        foreach (var participant in _viewModel.Participants)
        {
            participant.IsSpeaking = speaking.Contains(participant.UserId) && !(skipSelf && participant.IsSelf);
        }
    }

    /// <summary>Circulo com degrade e a inicial, no tamanho em que os avatares reais sao decodificados.</summary>
    private static BitmapSource RenderAvatar(string name, Color from, Color to)
    {
        const int size = 96;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
            dc.DrawEllipse(fill, null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);

            var text = new FormattedText(
                name[..1],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                size * 0.46,
                Brushes.White,
                1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose() => _timer.Stop();
}
