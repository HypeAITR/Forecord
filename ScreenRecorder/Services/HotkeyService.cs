using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenRecorder.Services;

public class HotkeyService : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_SHIFT   = 0x0004;
    private const int  WM_HOTKEY   = 0x0312;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 9000;

    public void Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// Örnek format: "Ctrl+Alt+R"
    /// </summary>
    public bool Register(string hotkey, Action callback)
    {
        if (!Parse(hotkey, out uint mod, out uint vk)) return false;

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mod, vk)) return false;

        _callbacks[id] = callback;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys)
            UnregisterHotKey(_hwnd, id);
        _callbacks.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var cb))
        {
            cb();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static bool Parse(string hotkey, out uint mod, out uint vk)
    {
        mod = 0; vk = 0;
        foreach (var part in hotkey.Split('+'))
        {
            switch (part.Trim().ToUpperInvariant())
            {
                case "CTRL":  mod |= MOD_CONTROL; break;
                case "ALT":   mod |= MOD_ALT;     break;
                case "SHIFT": mod |= MOD_SHIFT;   break;
                default:
                    if (Enum.TryParse<Key>(part.Trim(), true, out var key))
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    else
                        return false;
                    break;
            }
        }
        return vk != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }
}
