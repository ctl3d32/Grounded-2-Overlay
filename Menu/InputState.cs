using G2Overlay.Memory;

namespace G2Overlay.Menu;

/// <summary>
/// Entrada global por polling (GetAsyncKeyState): funciona mesmo com o foco no jogo.
/// Coordenadas do mouse relativas ao canto superior esquerdo do overlay.
/// </summary>
public sealed class InputState
{
    private readonly bool[] _prev = new bool[256];
    private readonly bool[] _cur = new bool[256];
    private static readonly int[] Tracked = { Native.VK_LBUTTON, Native.VK_INSERT, Native.VK_END, Native.VK_F9 };

    public float MouseX { get; private set; }
    public float MouseY { get; private set; }
    public bool MouseDown => _cur[Native.VK_LBUTTON];
    public bool MouseClicked => _cur[Native.VK_LBUTTON] && !_prev[Native.VK_LBUTTON];
    public bool MouseReleased => !_cur[Native.VK_LBUTTON] && _prev[Native.VK_LBUTTON];

    public void Update(IntPtr overlayHwnd)
    {
        foreach (int vk in Tracked)
        {
            _prev[vk] = _cur[vk];
            _cur[vk] = Native.IsKeyDown(vk);
        }

        if (Native.GetCursorPos(out var p))
        {
            Native.ScreenToClient(overlayHwnd, ref p);
            MouseX = p.X;
            MouseY = p.Y;
        }
    }

    public bool Pressed(int vk) => _cur[vk] && !_prev[vk];
}
