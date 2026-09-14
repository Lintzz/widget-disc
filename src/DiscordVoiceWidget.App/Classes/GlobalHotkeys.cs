using System.Windows.Input;
using System.Windows.Interop;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>Combinacao de teclas de um atalho, como "Ctrl+Shift+O".</summary>
public readonly record struct HotkeyBinding(ModifierKeys Modifiers, Key Key)
{
    public static readonly HotkeyBinding None = default;

    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return string.Empty;

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Le "Ctrl+Shift+O"; texto vazio ou invalido vira <see cref="None"/>.</summary>
    public static HotkeyBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        var modifiers = ModifierKeys.None;
        var key = Key.None;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (raw.Length == 1 && char.IsAsciiDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, ignoreCase: true, out key)) return None;
                    break;
            }
        }

        return IsAcceptable(modifiers, key) ? new HotkeyBinding(modifiers, key) : None;
    }

    /// <summary>
    /// Atalho global precisa de ao menos um modificador: sozinha, uma letra seria roubada
    /// de todos os programas - inclusive do chat do jogo.
    /// </summary>
    public static bool IsAcceptable(ModifierKeys modifiers, Key key)
        => modifiers != ModifierKeys.None
           && key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
               or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System);

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };
}

/// <summary>
/// Atalhos de teclado globais via RegisterHotKey.
///
/// O Windows entrega WM_HOTKEY so quando a combinacao e pressionada: nao ha hook de
/// teclado, nenhuma leitura de teclas digitadas e custo zero no resto do tempo. Usa
/// uma janela message-only propria, que nunca aparece.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public GlobalHotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("DiscordVoiceWidgetHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HWND_MESSAGE,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>
    /// Registra o atalho. Devolve false se outro programa ja usa a mesma combinacao -
    /// o Windows so permite um dono por atalho global.
    /// </summary>
    public bool Register(HotkeyBinding binding, Action action)
    {
        if (binding.IsEmpty) return true;

        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        if (!RegisterHotKey(_window.Handle, id, ToNative(binding.Modifiers) | MOD_NOREPEAT, vk)) return false;

        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint ToNative(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= MOD_WIN;
        return result;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
