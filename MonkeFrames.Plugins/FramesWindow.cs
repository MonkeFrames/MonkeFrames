using System;
using System.Numerics;

namespace MonkeFrames.Plugins;

public class FramesWindow
{
    public string Name { get; private set; }

    public Vector2 Size { get; private set; }
    public bool Showing { get; private set; } = false;

    public Action OnDraw = () => {};
    public Action OnOpen = () => {};
    public Action OnClose = () => {};

    public void Show()
    {
        Showing = true;
    }

    public void Hide()
    {
        Showing = false;
    }

    internal FramesWindow(string name, Vector2 size, Action onOpen = null!, Action onClose = null!, Action onDraw = null!)
    {
        Name = name;
        Size = size;

        if (onOpen is not null) OnOpen = onOpen;
        if (onClose is not null) OnClose = onClose;
        if (onDraw is not null) OnDraw = onDraw;
    }
}