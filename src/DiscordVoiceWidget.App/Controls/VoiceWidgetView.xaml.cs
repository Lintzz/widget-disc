using System.Windows;
using System.Windows.Controls;

namespace DiscordVoiceWidget.App;

public partial class VoiceWidgetView : UserControl
{
    /// <summary>
    /// Visual da barra de tarefas: cantos de 6px e fundo claro translucido, no padrao
    /// dos widgets do Windows 11. Desligado (overlay), fica a pilula escura opaca.
    /// </summary>
    public static readonly DependencyProperty TaskbarStyleProperty =
        DependencyProperty.Register(nameof(TaskbarStyle), typeof(bool), typeof(VoiceWidgetView),
            new PropertyMetadata(false));

    public bool TaskbarStyle
    {
        get => (bool)GetValue(TaskbarStyleProperty);
        set => SetValue(TaskbarStyleProperty, value);
    }

    public VoiceWidgetView() => InitializeComponent();
}
