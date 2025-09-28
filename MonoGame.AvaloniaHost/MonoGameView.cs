using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Microsoft.Xna.Framework;

namespace MonoGame.AvaloniaHost
{
    /// <summary>
    /// Хост MonoGame внутри Avalonia.
    /// </summary>
    public sealed class MonoGameView : OpenGlControlBase
    {
        public Game? Game { get; set; }

        private MonoGameCpuRenderer? _renderer;

        private static void ComputePixelSize(Visual visual, Rect bounds,
            out int pixelW, out int pixelH, out float scale)
        {
            var tl = TopLevel.GetTopLevel(visual);
            scale = (float)(tl?.RenderScaling ?? 1.0);

            int logicalW = Math.Max(1, (int)Math.Ceiling(bounds.Width));
            int logicalH = Math.Max(1, (int)Math.Ceiling(bounds.Height));

            pixelW = Math.Max(1, (int)MathF.Ceiling(logicalW * scale));
            pixelH = Math.Max(1, (int)MathF.Ceiling(logicalH * scale));
        }

        protected override void OnOpenGlInit(GlInterface gl)
        {
            if (Game is null)
                throw new InvalidOperationException("MonoGameView.Game must be set before initialization.");

            // ввод больше не пробрасываем, фокус не обязателен
            Focusable = true;

            ComputePixelSize(this, Bounds, out var pw, out var ph, out var scale);

            _renderer = new MonoGameCpuRenderer(Game);
            _renderer.Init(gl, pw, ph, scale);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_renderer is null) return;

            ComputePixelSize(this, Bounds, out var pw, out var ph, out var scale);

            _renderer.Render(gl, fb, pw, ph, scale, 0.0);
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

            if (change.Property == BoundsProperty && IsEffectivelyVisible && _renderer is not null)
            {
                ComputePixelSize(this, Bounds, out var pw, out var ph, out var scale);
                _renderer.RequestResize(pw, ph, scale);
            }
        }
    }
}
