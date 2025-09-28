using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MonoGame.AvaloniaHost.Sample;

public class Game1 : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;

    // --- Базовые текстуры ---
    private Texture2D _px;
    private Texture2D _circleTex;
    private Texture2D _triTex;
    private Texture2D _noiseTile;

    // --- FPS ---
    private double _fpsAccum;
    private int _fpsFrames;
    private float _fps;
    private float _ms;

    // --- Фигуры ---
    private enum ShapeType { Circle, Rect, Triangle }
    private struct Shape
    {
        public ShapeType Type;
        public Vector2 Pos;
        public Vector2 Vel;
        public float Size;
        public float Angle;
        public float Spin;
        public Color Color;
    }
    private readonly List<Shape> _shapes = new();

    // --- Частицы ---
    private struct Particle
    {
        public Vector2 Pos, Vel;
        public float Life, MaxLife, Size;
        public Color Col;
        public float Rot, RotVel;
    }
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();

    // --- «Шрифт» 3x5 ---
    private TinyFont _font;

    // Текущий размер вьюпорта (обновляем каждый кадр)
    private int ViewW => GraphicsDevice?.Viewport.Width ?? 0;
    private int ViewH => GraphicsDevice?.Viewport.Height ?? 0;

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "MonoGame FX — Full-Window World (no Content)";
    }

    protected override void Initialize()
    {
        base.Initialize();
        SpawnShapes(24);
        Window.ClientSizeChanged += (_, __) =>
        {
            // Ничего пересчитывать не нужно: мы используем динамический Viewport.
            // Но можно чуть подбросить фигур при резком увеличении окна:
            if (ViewW * ViewH > 1600 * 900) SpawnShapes(4);
        };
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        _px = new Texture2D(GraphicsDevice, 1, 1);
        _px.SetData(new[] { Color.White });

        _circleTex = CreateCircleTexture(32, 1.5f, Color.White);
        _triTex = CreateTriangleTexture(64, Color.White);
        _noiseTile = CreateNoiseTile(64, 64, new Color(18, 18, 22), new Color(28, 28, 34));

        _font = new TinyFont(_px);
    }

    protected override void Update(GameTime gameTime)
    {
        var k = Keyboard.GetState();
        if (k.IsKeyDown(Keys.Escape)) Exit();

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        dt = Math.Clamp(dt, 0f, 1f / 15f);

        // FPS
        _fpsAccum += gameTime.ElapsedGameTime.TotalMilliseconds;
        _fpsFrames++;
        if (_fpsAccum >= 250)
        {
            _ms = (float)(_fpsAccum / _fpsFrames);
            _fps = 1000f / Math.Max(0.0001f, _ms);
            _fpsAccum = 0;
            _fpsFrames = 0;
        }

        // Мышь: ЛКМ — частицы, ПКМ — фигуры
        var m = Mouse.GetState();
        var mousePos = m.Position.ToVector2();
        if (m.LeftButton == ButtonState.Pressed) Burst(mousePos, 50);
        if (m.RightButton == ButtonState.Pressed) SpawnShapes(1, mousePos);

        UpdateShapes(dt);
        UpdateParticles(dt);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(10, 10, 12));

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);

        // Фон на полный экран
        for (int x = 0; x < ViewW; x += _noiseTile.Width)
        for (int y = 0; y < ViewH; y += _noiseTile.Height)
            _spriteBatch.Draw(_noiseTile, new Vector2(x, y), Color.White);

        // Частицы
        foreach (var p in _particles)
        {
            float t = p.Life / p.MaxLife;
            float a = 1f - t;
            int s = Math.Max(1, (int)(p.Size * (0.4f + 0.6f * (1f - t))));
            var r = new Rectangle((int)(p.Pos.X - s / 2f), (int)(p.Pos.Y - s / 2f), s, s);
            _spriteBatch.Draw(_px, r, null, p.Col * a, p.Rot, Vector2.Zero, SpriteEffects.None, 0f);
        }

        // Фигуры
        foreach (var s in _shapes)
        {
            switch (s.Type)
            {
                case ShapeType.Circle:
                    float scaleC = s.Size / 32f;
                    _spriteBatch.Draw(_circleTex, s.Pos, null, s.Color, 0f,
                        new Vector2(_circleTex.Width / 2f, _circleTex.Height / 2f),
                        scaleC, SpriteEffects.None, 0f);
                    break;

                case ShapeType.Rect:
                    var rect = new Rectangle((int)(s.Pos.X - s.Size), (int)(s.Pos.Y - s.Size), (int)(s.Size * 2), (int)(s.Size * 2));
                    _spriteBatch.Draw(_px, rect, null, s.Color, s.Angle, Vector2.One, SpriteEffects.None, 0f);
                    break;

                case ShapeType.Triangle:
                    float scaleT = (s.Size * 2f) / 64f;
                    _spriteBatch.Draw(_triTex, s.Pos, null, s.Color, s.Angle,
                        new Vector2(_triTex.Width / 2f, _triTex.Height * 0.66f),
                        scaleT, SpriteEffects.None, 0f);
                    break;
            }
        }

        // HUD
        string hud = $"FPS {_fps:0.0}  |  ms {_ms:0.0}  |  {ViewW}x{ViewH}";
        var bgSize = _font.Measure(hud, 3);
        _spriteBatch.Draw(_px, new Rectangle(10, 10, bgSize.X + 12, bgSize.Y + 12), new Color(0, 0, 0, 160));
        _font.DrawString(_spriteBatch, hud, new Vector2(16, 16), 3, Color.White);

        _spriteBatch.End();

        base.Draw(gameTime);
    }

    // ------------------------
    // Симуляция фигур и частиц
    // ------------------------

    private void UpdateShapes(float dt)
    {
        for (int i = 0; i < _shapes.Count; i++)
        {
            var s = _shapes[i];
            s.Pos += s.Vel * dt;

            // Лёгкая «дрожь»
            float wiggle = (float)Math.Sin((i * 0.37f) + TotalSeconds() * (0.5f + (i % 5) * 0.1f));
            s.Vel += new Vector2(wiggle * 10f, -wiggle * 5f) * dt;

            s.Angle += s.Spin * dt;

            // Отскоки от динамических границ окна
            float maxX = Math.Max(1, ViewW - s.Size);
            float maxY = Math.Max(1, ViewH - s.Size);
            if (s.Pos.X < s.Size) { s.Pos.X = s.Size; s.Vel.X = Math.Abs(s.Vel.X) * 0.95f; }
            if (s.Pos.X > maxX)   { s.Pos.X = maxX;   s.Vel.X = -Math.Abs(s.Vel.X) * 0.95f; }
            if (s.Pos.Y < s.Size) { s.Pos.Y = s.Size; s.Vel.Y = Math.Abs(s.Vel.Y) * 0.95f; }
            if (s.Pos.Y > maxY)   { s.Pos.Y = maxY;   s.Vel.Y = -Math.Abs(s.Vel.Y) * 0.95f; }

            s.Vel *= 0.999f;

            EmitTrail(ref s);
            _shapes[i] = s;
        }
    }

    private void EmitTrail(ref Shape s)
    {
        int count = s.Type switch { ShapeType.Circle => 1, ShapeType.Rect => 2, _ => 3 };
        float spd = s.Vel.Length();
        float chance = Math.Clamp(spd / 600f, 0.1f, 1f);

        for (int n = 0; n < count; n++)
        {
            if (_rng.NextDouble() > chance) continue;

            var jitter = new Vector2(
                (float)(_rng.NextDouble() - 0.5) * s.Size * 0.3f,
                (float)(_rng.NextDouble() - 0.5) * s.Size * 0.3f);

            var p = new Particle
            {
                Pos = s.Pos + jitter,
                Vel = -s.Vel * 0.08f + jitter * 0.5f,
                Life = 0f,
                MaxLife = 0.8f + (float)_rng.NextDouble() * 0.6f,
                Size = 3f + (float)_rng.NextDouble() * (s.Size * 0.12f + 2f),
                Col = s.Color * 0.9f,
                Rot = (float)_rng.NextDouble() * MathF.PI,
                RotVel = ((float)_rng.NextDouble() - 0.5f) * 2f
            };
            _particles.Add(p);
        }
    }

    private void UpdateParticles(float dt)
    {
        // Обрезаем частицы, вылетевшие далеко за экран, чтобы не накапливались при ресайзе
        float killMargin = 128f;

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life += dt;
            p.Pos += p.Vel * dt;
            p.Vel *= 0.985f;
            p.Rot += p.RotVel * dt;
            p.Vel += new Vector2(0f, -6f) * dt;

            bool offscreen =
                p.Pos.X < -killMargin || p.Pos.X > ViewW + killMargin ||
                p.Pos.Y < -killMargin || p.Pos.Y > ViewH + killMargin;

            if (p.Life >= p.MaxLife || offscreen) _particles.RemoveAt(i);
            else _particles[i] = p;
        }
    }

    private void SpawnShapes(int count, Vector2? at = null)
    {
        for (int i = 0; i < count; i++)
        {
            var t = (ShapeType)(_rng.Next(3));
            float size = 12f + (float)_rng.NextDouble() * 28f;

            // Позиция и скорость относительно текущего окна
            var pos = at ?? new Vector2(
                80 + (float)_rng.NextDouble() * Math.Max(80, ViewW - 160),
                80 + (float)_rng.NextDouble() * Math.Max(80, ViewH - 160));

            var vel = new Vector2(
                (float)(_rng.NextDouble() * 2 - 1) * (100 + _rng.Next(150)),
                (float)(_rng.NextDouble() * 2 - 1) * (100 + _rng.Next(150)));

            var color = RandomNiceColor();

            var s = new Shape
            {
                Type = t,
                Pos = pos,
                Vel = vel,
                Size = size,
                Angle = (float)_rng.NextDouble() * MathF.Tau,
                Spin = ((float)_rng.NextDouble() * 2f - 1f) * 2f,
                Color = color
            };
            _shapes.Add(s);

            Burst(pos, 12);
        }
    }

    private void Burst(Vector2 center, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float a = (float)(_rng.NextDouble() * MathF.Tau);
            float sp = 120f + (float)_rng.NextDouble() * 220f;
            var v = new Vector2(MathF.Cos(a), MathF.Sin(a)) * sp;

            _particles.Add(new Particle
            {
                Pos = center,
                Vel = v,
                Life = 0f,
                MaxLife = 0.5f + (float)_rng.NextDouble() * 0.9f,
                Size = 3f + (float)_rng.NextDouble() * 6f,
                Col = RandomNiceColor() * 0.9f,
                Rot = a,
                RotVel = ((float)_rng.NextDouble() - 0.5f) * 6f
            });
        }
    }

    // ------------------------
    // Вспомогательные генераторы
    // ------------------------

    private Texture2D CreateNoiseTile(int w, int h, Color a, Color b)
    {
        var tex = new Texture2D(GraphicsDevice, w, h);
        var data = new Color[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int seed = x * 73856093 ^ y * 19349663;
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            float t = ((seed & 0xFFFF) / 65535f) * 0.9f + 0.05f;
            data[y * w + x] = LerpColor(a, b, t);
        }
        tex.SetData(data);
        return tex;
    }

    private Texture2D CreateCircleTexture(int radius, float feather, Color color)
    {
        int size = radius * 2;
        var tex = new Texture2D(GraphicsDevice, size, size);
        var data = new Color[size * size];

        float r = radius - 0.5f;
        float f = Math.Max(0.0001f, feather);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x + 0.5f - radius;
            float dy = y + 0.5f - radius;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float alpha = MathHelper.Clamp(1f - (dist - r) / f, 0f, 1f);
            data[y * size + x] = new Color(color.R, color.G, color.B) * alpha;
        }
        tex.SetData(data);
        return tex;
    }

    private Texture2D CreateTriangleTexture(int size, Color color)
    {
        var tex = new Texture2D(GraphicsDevice, size, size);
        var data = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            float t = y / (float)(size - 1);
            int half = (int)(t * (size / 2f));
            int cx = size / 2;
            int x0 = cx - half;
            int x1 = cx + half;
            for (int x = x0; x <= x1; x++)
            {
                int idx = y * size + Math.Clamp(x, 0, size - 1);
                data[idx] = color;
            }
        }
        tex.SetData(data);
        return tex;
    }

    private static Color LerpColor(Color c1, Color c2, float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        byte r = (byte)Math.Round(c1.R + (c2.R - c1.R) * t);
        byte g = (byte)Math.Round(c1.G + (c2.G - c1.G) * t);
        byte b = (byte)Math.Round(c1.B + (c2.B - c1.B) * t);
        return new Color(r, g, b);
    }

    private Color RandomNiceColor()
    {
        Color a = new Color(90, 180, 255);
        Color b = new Color(255, 120, 200);
        float t = (float)_rng.NextDouble();
        var c = LerpColor(a, b, t);
        float mul = 0.6f + (float)_rng.NextDouble() * 0.6f;
        return new Color((byte)(c.R * mul), (byte)(c.G * mul), (byte)(c.B * mul));
    }

    private float TotalSeconds()
    {
        return (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks).TotalSeconds;
    }

    // ------------------------
    // Мини-шрифт 3x5
    // ------------------------
    private class TinyFont
    {
        private readonly Texture2D _px;
        private readonly Dictionary<char, string[]> _glyphs = new();

        public TinyFont(Texture2D px)
        {
            _px = px;
            void G(char ch, string[] rows) => _glyphs[ch] = rows;

            G('0', new[] { "XXX", "X.X", "X.X", "X.X", "XXX" });
            G('1', new[] { "..X", "..X", "..X", "..X", "..X" });
            G('2', new[] { "XXX", "..X", "XXX", "X..", "XXX" });
            G('3', new[] { "XXX", "..X", "XXX", "..X", "XXX" });
            G('4', new[] { "X.X", "X.X", "XXX", "..X", "..X" });
            G('5', new[] { "XXX", "X..", "XXX", "..X", "XXX" });
            G('6', new[] { "XXX", "X..", "XXX", "X.X", "XXX" });
            G('7', new[] { "XXX", "..X", "..X", "..X", "..X" });
            G('8', new[] { "XXX", "X.X", "XXX", "X.X", "XXX" });
            G('9', new[] { "XXX", "X.X", "XXX", "..X", "XXX" });
            G('F', new[] { "XXX", "X..", "XXX", "X..", "X.." });
            G('P', new[] { "XXX", "X.X", "XXX", "X..", "X.." });
            G('S', new[] { "XXX", "X..", "XXX", "..X", "XXX" });
            G('m', new[] { "...", "XX.", "X.X", "X.X", "X.X" });
            G('s', new[] { "...", "...", "XX.", ".X.", "XX." });
            G('.', new[] { "...", "...", "...", "...", ".X." });
            G(':', new[] { "...", ".X.", "...", ".X.", "..." });
            G('|', new[] { ".X.", ".X.", ".X.", ".X.", ".X." });
            G(' ', new[] { "...", "...", "...", "...", "..." });
        }

        public void DrawString(SpriteBatch sb, string text, Vector2 pos, int scale, Color color)
        {
            int w = 3, h = 5, spacing = 1 * scale;
            var cursor = pos;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    cursor.X = pos.X;
                    cursor.Y += (h * scale) + spacing;
                    continue;
                }
                if (!_glyphs.TryGetValue(ch, out var g))
                {
                    cursor.X += (w * scale) + spacing;
                    continue;
                }

                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (g[y][x] == 'X')
                    {
                        var r = new Rectangle(
                            (int)cursor.X + x * scale,
                            (int)cursor.Y + y * scale,
                            scale, scale);
                        sb.Draw(_px, r, color);
                    }
                }
                cursor.X += (w * scale) + spacing;
            }
        }

        public Point Measure(string text, int scale)
        {
            int w = 3, h = 5, spacing = 1 * scale;
            int maxW = 0, curW = 0, totalH = (h * scale);
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    maxW = Math.Max(maxW, curW);
                    curW = 0;
                    totalH += (h * scale) + spacing;
                }
                else
                {
                    curW += (w * scale) + spacing;
                }
            }
            maxW = Math.Max(maxW, curW);
            return new Point(maxW, totalH);
        }
    }
}
