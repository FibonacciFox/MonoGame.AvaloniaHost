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

    // --- Карта ---
    const int TileSize = 32;
    const int MapW = 28;
    const int MapH = 16;
    private bool[,] _walls;

    // --- Текстуры, создаваемые на лету ---
    private Texture2D _px;           // 1x1 белый пиксель
    private Texture2D _circle24;     // кружок для слизня
    private Texture2D _noiseTile;    // «каменная» плитка

    // --- Игрок (слизень) ---
    private Vector2 _pos;     // центр
    private Vector2 _vel;
    private float _radius = 12f;
    private float _moveAccel = 1600f;
    private float _maxSpeed = 180f;
    private float _friction = 6f;

    // Дэш
    private bool _isDashing = false;
    private float _dashTimer = 0f;
    private float _dashCooldown = 0f;

    // Частицы «слизи»
    private struct Particle
    {
        public Vector2 Pos, Vel;
        public float Life, MaxLife, Size;
        public Color Col;
    }
    private readonly List<Particle> _particles = new();

    // Рандом
    private readonly Random _rng = new();

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = MapW * TileSize,
            PreferredBackBufferHeight = MapH * TileSize,
            IsFullScreen = false,
            SynchronizeWithVerticalRetrace = true
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "Dungeon Slime — MonoGame (без Content)";
    }

    protected override void Initialize()
    {
        base.Initialize();

        // Генерация карты: рамка-стены + немного внутреннего шума
        _walls = new bool[MapW, MapH];
        for (int x = 0; x < MapW; x++)
        for (int y = 0; y < MapH; y++)
        {
            bool border = x == 0 || y == 0 || x == MapW - 1 || y == MapH - 1;
            bool noise = _rng.NextDouble() < 0.10; // 10% препятствий
            _walls[x, y] = border || noise;
        }

        // Стартовая позиция — центр свободного тайла
        var spot = FindOpenSpot();
        _pos = spot.ToVector2() * TileSize + new Vector2(TileSize / 2f);
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        // 1x1 пиксель
        _px = new Texture2D(GraphicsDevice, 1, 1);
        _px.SetData(new[] { Color.White });

        // Кружок для слизня
        _circle24 = CreateCircleTexture((int)_radius, feather: 1.5f, color: Color.White);

        // «Шумная» плитка пола (процедурная)
        _noiseTile = CreateNoiseTile(TileSize, TileSize, new Color(30, 30, 35), new Color(45, 45, 52));
    }

    protected override void Update(GameTime gameTime)
    {
        var k = Keyboard.GetState();
        if (k.IsKeyDown(Keys.Escape))
            Exit();

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        HandleInput(k, dt);
        Simulate(dt);
        SpawnTrail(dt);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(18, 18, 22));

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);

        // Рисуем пол
        for (int x = 0; x < MapW; x++)
        for (int y = 0; y < MapH; y++)
        {
            var dst = new Rectangle(x * TileSize, y * TileSize, TileSize, TileSize);
            _spriteBatch.Draw(_noiseTile, dst, Color.White);
        }

        // Рисуем стены
        for (int x = 0; x < MapW; x++)
        for (int y = 0; y < MapH; y++)
        {
            if (!_walls[x, y]) continue;
            var r = new Rectangle(x * TileSize, y * TileSize, TileSize, TileSize);
            _spriteBatch.Draw(_px, r, new Color(12, 12, 16));
            var inner = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            _spriteBatch.Draw(_px, inner, new Color(50, 50, 60));
        }

        // Частицы
        foreach (var p in _particles)
        {
            float t = p.Life / p.MaxLife;
            var c = p.Col * (1f - t);
            int s = (int)Math.Max(1, p.Size * (1f - t));
            var dst = new Rectangle((int)(p.Pos.X - s / 2f), (int)(p.Pos.Y - s / 2f), s, s);
            _spriteBatch.Draw(_px, dst, c);
        }

        // Слизень (тень + тело)
        _spriteBatch.Draw(_circle24, new Vector2(_pos.X - _radius, _pos.Y - _radius + 3), Color.Black * 0.35f);
        var slimeColor = _isDashing ? new Color(140, 230, 200) : new Color(120, 200, 170);
        _spriteBatch.Draw(_circle24, new Vector2(_pos.X - _radius, _pos.Y - _radius), slimeColor);

        // Глазки
        var forward = _vel.LengthSquared() > 1f ? Vector2.Normalize(_vel) : new Vector2(0, -1);
        var eyeOffset = new Vector2(-forward.Y, forward.X) * 4f;
        DrawDot(_pos + forward * 4f + eyeOffset, 3, Color.Black);
        DrawDot(_pos + forward * 4f - eyeOffset, 3, Color.Black);

        _spriteBatch.End();

        base.Draw(gameTime);
    }

    // -------------------
    // ВСПОМОГАТЕЛЬНОЕ
    // -------------------

    private void HandleInput(KeyboardState k, float dt)
    {
        Vector2 dir = Vector2.Zero;
        if (k.IsKeyDown(Keys.A) || k.IsKeyDown(Keys.Left)) dir.X -= 1;
        if (k.IsKeyDown(Keys.D) || k.IsKeyDown(Keys.Right)) dir.X += 1;
        if (k.IsKeyDown(Keys.W) || k.IsKeyDown(Keys.Up)) dir.Y -= 1;
        if (k.IsKeyDown(Keys.S) || k.IsKeyDown(Keys.Down)) dir.Y += 1;

        if (dir != Vector2.Zero)
        {
            dir.Normalize();
            float accel = _isDashing ? _moveAccel * 0.4f : _moveAccel;
            _vel += dir * accel * dt;
        }

        // Дэш по Space
        if (!_isDashing && _dashCooldown <= 0f && k.IsKeyDown(Keys.Space))
        {
            Vector2 dashDir = _vel;
            if (dashDir.LengthSquared() < 0.01f)
                dashDir = new Vector2(0, -1);
            dashDir.Normalize();

            _vel = dashDir * 520f;
            _isDashing = true;
            _dashTimer = 0.12f;    // длительность
            _dashCooldown = 0.45f; // КД
        }
    }

    private void Simulate(float dt)
    {
        // Таймеры дэша
        if (_isDashing)
        {
            _dashTimer -= dt;
            if (_dashTimer <= 0f) _isDashing = false;
        }
        if (_dashCooldown > 0f) _dashCooldown -= dt;

        // Ограничение скорости
        float maxSpd = _isDashing ? 560f : _maxSpeed;
        if (_vel.Length() > maxSpd)
            _vel = Vector2.Normalize(_vel) * maxSpd;

        // Трение
        float fr = _isDashing ? _friction * 0.5f : _friction;
        _vel *= MathF.Max(0f, 1f - fr * dt);

        // Попытка движения с коллизиями по осям (слайдинг)
        Vector2 target = _pos + _vel * dt;
        _pos = MoveAxis(_pos, new Vector2(target.X, _pos.Y));
        _pos = MoveAxis(_pos, new Vector2(_pos.X, target.Y));

        // Держим внутри экрана
        _pos.X = MathHelper.Clamp(_pos.X, _radius, MapW * TileSize - _radius);
        _pos.Y = MathHelper.Clamp(_pos.Y, _radius, MapH * TileSize - _radius);
    }

    private Vector2 MoveAxis(Vector2 from, Vector2 to)
    {
        // Хитбокс слизня в целевой точке
        var rect = new Rectangle((int)(to.X - _radius), (int)(to.Y - _radius),
                                 (int)(_radius * 2), (int)(_radius * 2));

        // Диапазон тайлов для проверки
        int minTx = Math.Max(0, rect.Left / TileSize);
        int minTy = Math.Max(0, rect.Top / TileSize);
        int maxTx = Math.Min(MapW - 1, rect.Right / TileSize);
        int maxTy = Math.Min(MapH - 1, rect.Bottom / TileSize);

        // Если есть пересечение со стеной — откатываем движение по текущей оси
        for (int tx = minTx; tx <= maxTx; tx++)
        for (int ty = minTy; ty <= maxTy; ty++)
        {
            if (!_walls[tx, ty]) continue;
            var tileRect = new Rectangle(tx * TileSize, ty * TileSize, TileSize, TileSize);
            if (tileRect.Intersects(rect))
                return from;
        }

        return to;
    }

    private void SpawnTrail(float dt)
    {
        // Частица при движении
        if (_vel.LengthSquared() < 2f) return;

        // Несколько капель
        int count = _isDashing ? 3 : 1;
        for (int i = 0; i < count; i++)
        {
            var jitter = new Vector2(
                (float)(_rng.NextDouble() - 0.5) * 8f,
                (float)(_rng.NextDouble() - 0.5) * 8f);

            _particles.Add(new Particle
            {
                Pos = _pos + jitter,
                Vel = -_vel * 0.1f + jitter * 0.8f,
                Life = 0f,
                MaxLife = _isDashing ? 0.45f : 0.7f,
                Size = _isDashing ? 6f : 4f,
                Col = new Color(100, 200, 170)
            });
        }

        // Обновление и зачистка
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life += dt;
            p.Pos += p.Vel * dt;
            p.Vel *= 0.96f;
            if (p.Life >= p.MaxLife) _particles.RemoveAt(i);
            else _particles[i] = p;
        }
    }

    private Point FindOpenSpot()
    {
        // Поиск свободного тайла ближе к центру
        int cx = MapW / 2, cy = MapH / 2;
        int r = 0;
        while (r < Math.Max(MapW, MapH))
        {
            for (int x = Math.Max(1, cx - r); x <= Math.Min(MapW - 2, cx + r); x++)
            for (int y = Math.Max(1, cy - r); y <= Math.Min(MapH - 2, cy + r); y++)
                if (!_walls[x, y]) return new Point(x, y);
            r++;
        }
        return new Point(1, 1);
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

    private Texture2D CreateNoiseTile(int w, int h, Color a, Color b)
    {
        var tex = new Texture2D(GraphicsDevice, w, h);
        var data = new Color[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            // простой value-noise
            int seed = x * 73856093 ^ y * 19349663;
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            float t = ((seed & 0xFFFF) / 65535f) * 0.8f + 0.1f;
            data[y * w + x] = LerpColor(a, b, t);
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

    private void DrawDot(Vector2 pos, int size, Color color)
    {
        var r = new Rectangle((int)(pos.X - size / 2f), (int)(pos.Y - size / 2f), size, size);
        _spriteBatch.Draw(_px, r, color);
    }
}
