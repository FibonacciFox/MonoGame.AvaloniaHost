using Microsoft.Xna.Framework.Graphics;

namespace MonoGame.AvaloniaHost
{
    /// <summary>
    /// Рендерер публикует сюда offscreen RenderTarget2D,
    /// игра берёт его и рендерит в Draw().
    /// </summary>
    public interface IExternalRenderTargetProvider
    {
        RenderTarget2D? CurrentRt { get; }
        int Width { get; }
        int Height { get; }
    }
}