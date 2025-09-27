using System;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Microsoft.Xna.Framework;

namespace MonoGame.AvaloniaHost;

/// <summary>
/// RU: Хост MonoGame внутри Avalonia. Все GL-действия выполняются строго в GL-потоке контрола.
/// EN: MonoGame host inside Avalonia. All GL operations happen strictly on the control's GL thread.
/// </summary>
public sealed class MonoGameView : OpenGlControlBase
{
    /// <summary>MonoGame Game (its ctor must create <c>new GraphicsDeviceManager(this)</c>).</summary>
    public Game? Game { get; set; }

    private MonoGameCpuRenderer? _renderer;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        if (Game is null)
            throw new InvalidOperationException("MonoGameView.Game must be set before initialization.");

        var w = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var h = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
        var scale = (float)(VisualRoot?.RenderScaling ?? 1f);

        _renderer = new MonoGameCpuRenderer(Game);
        _renderer.Init(gl, w, h, scale);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is null) return;

        var w = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var h = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
        var scale = (float)(VisualRoot?.RenderScaling ?? 1f);

        // Render() сам применяет отложенный ресайз по debounce на GL-потоке.
        _renderer.Render(gl, fb, w, h, scale, 0.0);

        // просим следующий кадр (continuous rendering)
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer?.Deinit(gl);
        _renderer?.Dispose();
        _renderer = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // ВАЖНО: никаких GL-вызовов в UI-потоке!
        if (change.Property == BoundsProperty && IsEffectivelyVisible && _renderer is not null)
        {
            var w = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
            var h = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
            var scale = (float)(VisualRoot?.RenderScaling ?? 1f);

            // только запросить ресайз; реальный GL-ресайз будет сделан в Render() после debounce
            _renderer.RequestResize(w, h, scale);
        }
    }
}
