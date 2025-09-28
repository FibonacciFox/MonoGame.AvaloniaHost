using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Avalonia.OpenGL;

namespace MonoGame.AvaloniaHost
{
    /// <summary>
    /// MonoGame → CPU double buffer → PBO(double) → glTexSubImage2D → fullscreen quad.
    /// Вариант для одного MonoGameView: кадр MonoGame считается в Render() Avalonia, PBO включены.
    /// </summary>
    public sealed class MonoGameCpuRenderer : IDisposable
    {
        private readonly Game _game;
        public MonoGameCpuRenderer(Game game) => _game = game ?? throw new ArgumentNullException(nameof(game));

        // ---- GL const ----
        private const int GL_TEXTURE_2D = 0x0DE1;
        private const int GL_ARRAY_BUFFER = 0x8892;
        private const int GL_STATIC_DRAW = 0x88E4;
        private const int GL_FLOAT = 0x1406;
        private const int GL_TRIANGLE_STRIP = 0x0005;
        private const int GL_RGBA = 0x1908;
        private const int GL_UNSIGNED_BYTE = 0x1401;
        private const int GL_TEXTURE_MIN_FILTER = 0x2801;
        private const int GL_TEXTURE_MAG_FILTER = 0x2800;
        private const int GL_TEXTURE_WRAP_S = 0x2802;
        private const int GL_TEXTURE_WRAP_T = 0x2803;
        private const int GL_CLAMP_TO_EDGE = 0x812F;
        private const int GL_LINEAR = 0x2601;
        private const int GL_VERTEX_SHADER = 0x8B31;
        private const int GL_FRAGMENT_SHADER = 0x8B30;
        private const int GL_COMPILE_STATUS = 0x8B81;
        private const int GL_INFO_LOG_LENGTH = 0x8B84;
        private const int GL_LINK_STATUS = 0x8B82;
        private const int GL_FRAMEBUFFER = 0x8D40;
        private const int GL_TEXTURE0 = 0x84C0;

        // ---- PBO ----
        private const int GL_PIXEL_UNPACK_BUFFER = 0x88EC;
        private const int GL_STREAM_DRAW = 0x88E0;
        private bool _usePbo;
        private int _pbo0, _pbo1;
        private int _pboWriteIdx;
        private int _pboSizeBytes;

        // ---- GL state ----
        private GlInterface? _gl;
        private int _prog, _vao, _vbo, _tex;
        private int _uTexLoc;
        private bool _hasVao;

        // delegates not exposed by GlInterface
        private delegate void Uniform1iDelegate(int location, int v0);
        private Uniform1iDelegate? _uniform1i;

        private delegate void TexSubImage2DDelegate(int target, int level, int xoff, int yoff, int w, int h, int format, int type, IntPtr data);
        private TexSubImage2DDelegate? _texSubImage2D;

        private delegate void BufferSubDataDelegate(int target, IntPtr offset, IntPtr size, IntPtr data);
        private BufferSubDataDelegate? _bufferSubData;

        // ---- size / resize ----
        private int _width = 1, _height = 1;
        private int _lastTexW, _lastTexH;
        private bool _resizePending;
        private int _pendingW, _pendingH;
        private float _pendingScale;

        // ---- CPU double buffer ----
        private byte[] _buf0 = Array.Empty<byte>();
        private byte[] _buf1 = Array.Empty<byte>();
        private GCHandle _pin0, _pin1;
        private IntPtr _ptr0, _ptr1;
        private int _writeIdx;        // куда пишет MG сейчас
        private volatile bool _hasPending;

        // diag
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private TimeSpan _last;

        // ============ API ============

        public void Init(GlInterface gl, int width, int height, float scale)
        {
            _gl = gl;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            Console.WriteLine($"[Host] GL: {gl.Renderer} v{gl.Version}");

            // PBO доступны в DesktopGL и ES 3.0+; отключим только на ES2.
            bool isEs2 = (gl.Version?.Contains("OpenGL ES 2", StringComparison.OrdinalIgnoreCase) ?? false)
                      || (gl.Renderer?.Contains("OpenGL ES 2", StringComparison.OrdinalIgnoreCase) ?? false);
            _usePbo = !isEs2;

            LoadProc(gl);
            CreateFullscreenQuad(gl);
            CreateTexture(gl, _width, _height);
            EnsureCpuBuffers(_width, _height);
            if (_usePbo) CreateOrResizePbos(gl, _width, _height);

            var gdm = (GraphicsDeviceManager?)_game.Services.GetService(typeof(IGraphicsDeviceManager))
                      ?? throw new InvalidOperationException("Game must have GraphicsDeviceManager(this) in ctor.");

            gdm.SynchronizeWithVerticalRetrace = false;
            gdm.IsFullScreen = false;
            gdm.PreferredBackBufferWidth = _width;
            gdm.PreferredBackBufferHeight = _height;
            gdm.ApplyChanges();

            _game.InactiveSleepTime = TimeSpan.Zero;

            _lastTexW = _width; _lastTexH = _height;
            Console.WriteLine($"[Host] Init done, size={_width}x{_height}, PBO={_usePbo}");
        }

        public void RequestResize(int width, int height, float scale)
        {
            _pendingW = Math.Max(1, width);
            _pendingH = Math.Max(1, height);
            _pendingScale = scale;
            _resizePending = true;
        }

        /// <summary>
        /// GL-поток Avalonia: 1) (возможный) ресайз MG → CPU буферы, 2) один кадр MG, 3) аплоад (PBO), 4) фуллскрин-квад.
        /// </summary>
        public void Render(GlInterface gl, int framebuffer, int width, int height, float scale, double timeSeconds)
        {
            // --- 0) применяем отложенный ресайз на MG ---
            if (_resizePending)
            {
                _resizePending = false;
                _width = _pendingW;
                _height = _pendingH;

                var gd = _game.GraphicsDevice;
                var pp = gd.PresentationParameters;
                pp.BackBufferWidth = _width;
                pp.BackBufferHeight = _height;
                gd.Reset(pp);
                gd.Viewport = new Viewport(0, 0, _width, _height);

                EnsureCpuBuffers(_width, _height);
                _hasPending = false; // новую текстуру пересоздадим ниже
            }

            // --- 1) синхронизация GL-ресурсов под текущее MG разрешение ---
            if (_tex == 0 || _width != _lastTexW || _height != _lastTexH)
            {
                RecreateTexture(gl, _width, _height);

                gl.BindTexture(GL_TEXTURE_2D, _tex);
                using (var zero = new UnmanagedBuffer(_width * _height * 4))
                    _texSubImage2D!.Invoke(GL_TEXTURE_2D, 0, 0, 0, _width, _height, GL_RGBA, GL_UNSIGNED_BYTE, zero.Ptr);

                _lastTexW = _width; _lastTexH = _height;
                if (_usePbo) CreateOrResizePbos(gl, _width, _height);
            }

            // --- 2) один кадр MonoGame → CPU buffer ---
            RunOneMonoGameFrame();

            // --- 3) upload CPU → (PBO) → Texture ---
            if (_hasPending)
            {
                int byteSize = _width * _height * 4;
                var readPtr = (_writeIdx == 0) ? _ptr1 : _ptr0; // берём противоположный

                if (_usePbo)
                {
                    int pbo = (_pboWriteIdx == 0) ? _pbo0 : _pbo1;
                    gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, pbo);

                    if (_bufferSubData != null)
                        _bufferSubData(GL_PIXEL_UNPACK_BUFFER, IntPtr.Zero, new IntPtr(byteSize), readPtr);
                    else
                        gl.BufferData(GL_PIXEL_UNPACK_BUFFER, new IntPtr(byteSize), readPtr, GL_STREAM_DRAW);

                    gl.BindTexture(GL_TEXTURE_2D, _tex);
                    _texSubImage2D!.Invoke(GL_TEXTURE_2D, 0, 0, 0, _width, _height, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);

                    gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
                    _pboWriteIdx ^= 1;
                }
                else
                {
                    gl.BindTexture(GL_TEXTURE_2D, _tex);
                    _texSubImage2D!.Invoke(GL_TEXTURE_2D, 0, 0, 0, _width, _height, GL_RGBA, GL_UNSIGNED_BYTE, readPtr);
                }
            }

            // --- 4) рисуем фуллскрин-квад в framebuffer Avalonia ---
            gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
            gl.Viewport(0, 0, width, height);

            gl.UseProgram(_prog);
            gl.ActiveTexture(GL_TEXTURE0);
            gl.BindTexture(GL_TEXTURE_2D, _tex);

            if (_hasVao)
            {
                gl.BindVertexArray(_vao);
                gl.DrawArrays(GL_TRIANGLE_STRIP, 0, 4);
            }
            else
            {
                gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
                gl.DrawArrays(GL_TRIANGLE_STRIP, 0, 4);
            }
        }

        public void Deinit(GlInterface gl) => Dispose();

        public void Dispose()
        {
            try
            {
                var gl = _gl;
                if (gl != null)
                {
                    if (_pbo0 != 0) { var b = _pbo0; unsafe { gl.DeleteBuffers(1, &b); } _pbo0 = 0; }
                    if (_pbo1 != 0) { var b = _pbo1; unsafe { gl.DeleteBuffers(1, &b); } _pbo1 = 0; }
                    if (_tex != 0) { gl.DeleteTexture(_tex); _tex = 0; }
                    if (_vbo != 0) { var b = _vbo; unsafe { gl.DeleteBuffers(1, &b); } _vbo = 0; }
                    if (_vao != 0 && gl.IsDeleteVertexArraysAvailable) { var a = _vao; unsafe { gl.DeleteVertexArrays(1, &a); } _vao = 0; }
                    if (_prog != 0) { gl.DeleteProgram(_prog); _prog = 0; }
                }
            }
            catch { /* ignore */ }

            if (_pin0.IsAllocated) _pin0.Free();
            if (_pin1.IsAllocated) _pin1.Free();
            _buf0 = Array.Empty<byte>();
            _buf1 = Array.Empty<byte>();
            _ptr0 = _ptr1 = IntPtr.Zero;
            _gl = null;
        }

        // ============ internals ============

        private void RunOneMonoGameFrame()
        {
            try { _game.RunOneFrame(); }
            catch (Exception ex) { Console.WriteLine("[Host][ERR] RunOneFrame: " + ex); }

            var writeArray = (_writeIdx == 0) ? _buf0 : _buf1;
            int total = _width * _height * 4;

            if (writeArray.Length < total)
                Array.Resize(ref writeArray, total);
            if (_writeIdx == 0) _buf0 = writeArray; else _buf1 = writeArray;

            _game.GraphicsDevice.GetBackBufferData(writeArray, 0, total);
            RefreshPinsIfResized();

            _hasPending = true;
            _writeIdx ^= 1;
        }

        private void EnsureCpuBuffers(int w, int h)
        {
            int bytes = Math.Max(1, w) * Math.Max(1, h) * 4;

            _buf0 = new byte[bytes];
            _buf1 = new byte[bytes];

            if (_pin0.IsAllocated) _pin0.Free();
            if (_pin1.IsAllocated) _pin1.Free();

            _pin0 = GCHandle.Alloc(_buf0, GCHandleType.Pinned);
            _pin1 = GCHandle.Alloc(_buf1, GCHandleType.Pinned);
            _ptr0 = _pin0.AddrOfPinnedObject();
            _ptr1 = _pin1.AddrOfPinnedObject();

            _hasPending = false;
            _writeIdx = 0;
        }

        private void RefreshPinsIfResized()
        {
            int bytes = _width * _height * 4;

            if (_buf0.Length != bytes)
            {
                if (_pin0.IsAllocated) _pin0.Free();
                _pin0 = GCHandle.Alloc(_buf0, GCHandleType.Pinned);
                _ptr0 = _pin0.AddrOfPinnedObject();
            }
            if (_buf1.Length != bytes)
            {
                if (_pin1.IsAllocated) _pin1.Free();
                _pin1 = GCHandle.Alloc(_buf1, GCHandleType.Pinned);
                _ptr1 = _pin1.AddrOfPinnedObject();
            }
        }

        private void LoadProc(GlInterface gl)
        {
            var p = gl.GetProcAddress("glTexSubImage2D");
            if (p == IntPtr.Zero) p = gl.GetProcAddress("glTexSubImage2DEXT");
            if (p == IntPtr.Zero) throw new NotSupportedException("glTexSubImage2D is required.");
            _texSubImage2D = (TexSubImage2DDelegate)Marshal.GetDelegateForFunctionPointer(p, typeof(TexSubImage2DDelegate));

            var pu = gl.GetProcAddress("glUniform1i");
            if (pu != IntPtr.Zero)
                _uniform1i = (Uniform1iDelegate)Marshal.GetDelegateForFunctionPointer(pu, typeof(Uniform1iDelegate));

            var ps = gl.GetProcAddress("glBufferSubData");
            if (ps != IntPtr.Zero)
                _bufferSubData = (BufferSubDataDelegate)Marshal.GetDelegateForFunctionPointer(ps, typeof(BufferSubDataDelegate));
        }

        private void CreateTexture(GlInterface gl, int w, int h)
        {
            if (_tex != 0) gl.DeleteTexture(_tex);
            _tex = gl.GenTexture();
            gl.BindTexture(GL_TEXTURE_2D, _tex);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
        }

        private void RecreateTexture(GlInterface gl, int w, int h)
        {
            CreateTexture(gl, w, h);
            _hasPending = false;
        }

        private void CreateOrResizePbos(GlInterface gl, int w, int h)
        {
            int needBytes = Math.Max(1, w) * Math.Max(1, h) * 4;
            if (_pbo0 != 0 && _pbo1 != 0 && _pboSizeBytes == needBytes) return;

            if (_pbo0 != 0) { var b = _pbo0; unsafe { gl.DeleteBuffers(1, &b); } _pbo0 = 0; }
            if (_pbo1 != 0) { var b = _pbo1; unsafe { gl.DeleteBuffers(1, &b); } _pbo1 = 0; }

            _pbo0 = gl.GenBuffer();
            _pbo1 = gl.GenBuffer();

            AllocatePbo(gl, _pbo0, needBytes);
            AllocatePbo(gl, _pbo1, needBytes);

            _pboSizeBytes = needBytes;
            _pboWriteIdx = 0;

            Console.WriteLine($"[Host] PBO ready size={_pboSizeBytes}");
        }

        private void AllocatePbo(GlInterface gl, int pbo, int bytes)
        {
            gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, pbo);
            gl.BufferData(GL_PIXEL_UNPACK_BUFFER, new IntPtr(bytes), IntPtr.Zero, GL_STREAM_DRAW);
            gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
        }

        private void CreateFullscreenQuad(GlInterface gl)
        {
            bool gles = (gl.Version?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) ?? false)
                     || (gl.Renderer?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) ?? false);

            string vs = gles
                ? @"#version 300 es
precision highp float;
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUv;
out vec2 vUv;
void main(){ vUv=vec2(aUv.x,1.0-aUv.y); gl_Position=vec4(aPos,0.0,1.0); }"
                : @"#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUv;
out vec2 vUv;
void main(){ vUv=vec2(aUv.x,1.0-aUv.y); gl_Position=vec4(aPos,0.0,1.0); }";

            string fs = gles
                ? @"#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uTex;
out vec4 frag;
void main(){ frag = texture(uTex, vUv); }"
                : @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
out vec4 fragColor;
void main(){ fragColor = texture(uTex, vUv); }";

            _prog = CompileProgram(gl, vs, fs);
            gl.UseProgram(_prog);
            _uTexLoc = gl.GetUniformLocationString(_prog, "uTex");
            if (_uniform1i != null) _uniform1i(_uTexLoc, 0); else gl.Uniform1f(_uTexLoc, 0f);

            float[] verts =
            {
                // pos      uv
                -1f, -1f,  0f, 0f,
                 1f, -1f,  1f, 0f,
                -1f,  1f,  0f, 1f,
                 1f,  1f,  1f, 1f,
            };

            if (gl.IsGenVertexArraysAvailable)
            {
                _vao = gl.GenVertexArray();
                _hasVao = _vao != 0;
            }
            if (_hasVao) gl.BindVertexArray(_vao);

            _vbo = gl.GenBuffer();
            gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);

            var handle = GCHandle.Alloc(verts, GCHandleType.Pinned);
            try
            {
                var size = new IntPtr(sizeof(float) * verts.Length);
                gl.BufferData(GL_ARRAY_BUFFER, size, handle.AddrOfPinnedObject(), GL_STATIC_DRAW);
            }
            finally { handle.Free(); }

            const int stride = 4 * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, stride, IntPtr.Zero);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, stride, new IntPtr(2 * sizeof(float)));
        }

        private static int CompileProgram(GlInterface gl, string vsSrc, string fsSrc)
        {
            int vs = gl.CreateShader(GL_VERTEX_SHADER);
            gl.ShaderSourceString(vs, vsSrc);
            gl.CompileShader(vs);
            CheckShader(gl, vs, "VS");

            int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
            gl.ShaderSourceString(fs, fsSrc);
            gl.CompileShader(fs);
            CheckShader(gl, fs, "FS");

            int prog = gl.CreateProgram();
            gl.AttachShader(prog, vs);
            gl.AttachShader(prog, fs);
            gl.LinkProgram(prog);
            CheckProgram(gl, prog);

            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            return prog;
        }

        private static void CheckShader(GlInterface gl, int sh, string tag)
        {
            unsafe
            {
                int ok; gl.GetShaderiv(sh, GL_COMPILE_STATUS, &ok);
                if (ok == 0)
                {
                    int len; gl.GetShaderiv(sh, GL_INFO_LOG_LENGTH, &len);
                    var buf = new byte[Math.Max(1, len)];
                    fixed (byte* p = buf) gl.GetShaderInfoLog(sh, buf.Length, out len, p);
                    var msg = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Max(0, len));
                    throw new Exception($"{tag}: {msg}");
                }
            }
        }

        private static void CheckProgram(GlInterface gl, int prog)
        {
            unsafe
            {
                int ok; gl.GetProgramiv(prog, GL_LINK_STATUS, &ok);
                if (ok == 0)
                {
                    int len; gl.GetProgramiv(prog, GL_INFO_LOG_LENGTH, &len);
                    var buf = new byte[Math.Max(1, len)];
                    fixed (byte* p = buf) gl.GetProgramInfoLog(prog, buf.Length, out len, p);
                    var msg = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Max(0, len));
                    throw new Exception($"PROG: {msg}");
                }
            }
        }

        private sealed class UnmanagedBuffer : IDisposable
        {
            public IntPtr Ptr { get; }
            private readonly int _size;
            public UnmanagedBuffer(int size)
            {
                _size = Math.Max(1, size);
                Ptr = Marshal.AllocHGlobal(_size);
                unsafe { new Span<byte>((void*)Ptr, _size).Clear(); }
            }
            public void Dispose() { if (Ptr != IntPtr.Zero) Marshal.FreeHGlobal(Ptr); }
        }
    }
}
