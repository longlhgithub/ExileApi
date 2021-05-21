using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Windows;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;
using Vector4 = System.Numerics.Vector4;

namespace ExileCore.RenderQ
{
    public class DX11 : IDisposable
    {
        private readonly RenderForm _form;
        private readonly SwapChain _swapChain;
        private readonly object _sync = new object();
        private Color4 _clearColor = new Color4(0, 0, 0, 0);
        private readonly Factory _factory;
        private readonly Stopwatch _sw;
        private double _debugTime;
        private readonly DebugInformation _coreTickDebug;
        private readonly DebugInformation _imGuiDebug;
        private readonly DebugInformation _spritesDebug;
        private readonly DebugInformation _swapchainDebug;
        private Viewport _viewport;

        public DX11(RenderForm form, CoreSettings coreSettings)
        {
            _form = form;
            _sw = Stopwatch.StartNew();
            LoadedTexturesByName = new Dictionary<string, ShaderResourceView>();
            LoadedTexturesByPtrs = new Dictionary<IntPtr, ShaderResourceView>();

            var swapChainDesc = new SwapChainDescription
            {
                Usage = Usage.RenderTargetOutput,
                OutputHandle = form.Handle,
                BufferCount = 1,
                IsWindowed = true,
                Flags = SwapChainFlags.AllowModeSwitch,
                SwapEffect = SwapEffect.Discard,
                SampleDescription = new SampleDescription(1, 0),
                ModeDescription = new ModeDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    Width = form.Width,
                    Height = form.Height,
                    Scaling = DisplayModeScaling.Unspecified,
                    RefreshRate = new Rational(60, 1),
                    ScanlineOrdering = DisplayModeScanlineOrder.Unspecified
                }
            };

            Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.None,
                new[] {FeatureLevel.Level_11_0, FeatureLevel.Level_10_0}, swapChainDesc, out var device,
                out var swapChain);

            D11Device = device;
            DeviceContext = device.ImmediateContext;
            _swapChain = swapChain;

            _factory = swapChain.GetParent<Factory>();
            _factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAll);
            BackBuffer = Resource.FromSwapChain<Texture2D>(swapChain, 0);
            RenderTargetView = new RenderTargetView(device, BackBuffer);

            using (new PerformanceTimer("Init ImGuiRender"))
            {
                ImGuiRender = new ImGuiRender(this, form, coreSettings);
            }

            using (new PerformanceTimer("Init SpriteRender"))
            {
                SpritesRender = new SpritesRender(this, form, coreSettings);
            }

            InitStates();

            form.UserResized += (sender, args) =>
            {
                RenderTargetView?.Dispose();
                BackBuffer.Dispose();

                swapChain.ResizeBuffers(1, form.Width, form.Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
                BackBuffer = Resource.FromSwapChain<Texture2D>(swapChain, 0);
                RenderTargetView = new RenderTargetView(device, BackBuffer);
                ImGuiRender.Resize(form.Bounds);
                ImGuiRender.UpdateConstantBuffer();
                SpritesRender.ResizeConstBuffer(BackBuffer.Description);
                var descp = BackBuffer.Description;
                _viewport.Height = form.Height;
                _viewport.Width = form.Width;
                DeviceContext.Rasterizer.SetViewport(_viewport);
                DeviceContext.OutputMerger.SetRenderTargets(RenderTargetView);
            };

            _coreTickDebug = new DebugInformation("CoreTick");
            _imGuiDebug = new DebugInformation("ImGui");
            _spritesDebug = new DebugInformation("Sprites");
            _swapchainDebug = new DebugInformation("Swapchain");

            // Core.DebugInformations.Add(ImGuiDebug);
            // Core.DebugInformations.Add(ImGuiInputDebug);
            // Core.DebugInformations.Add(SpritesDebug);
            // Core.DebugInformations.Add(SwapchainDebug);
        }

        public DeviceContext DeviceContext { get; }
        public Device D11Device { get; }
        public VertexShader VertexShader { get; set; }
        public PixelShader PixelShader { get; set; }
        public RenderTargetView RenderTargetView { get; set; }
        public InputLayout Layout { get; set; }
        public Texture2D BackBuffer { get; private set; }
        public bool VSync { get; set; } = false;
        private Dictionary<string, ShaderResourceView> LoadedTexturesByName { get; }
        private Dictionary<IntPtr, ShaderResourceView> LoadedTexturesByPtrs { get; }
        public ImGuiRender ImGuiRender { get; }
        public SpritesRender SpritesRender { get; }
        public int TextutresCount => LoadedTexturesByName.Count;

        public void Dispose()
        {
            RenderTargetView.Dispose();
            BackBuffer.Dispose();
            DeviceContext.Dispose();
            D11Device.Dispose();
            _swapChain.Dispose();
            _factory.Dispose();
        }

        private void InitStates()
        {
            //Debug if texture broken

            //Blend
            var blendStateDescription = new BlendStateDescription();
            blendStateDescription.RenderTarget[0].IsBlendEnabled = true;
            blendStateDescription.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
            blendStateDescription.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
            blendStateDescription.RenderTarget[0].BlendOperation = BlendOperation.Add;
            blendStateDescription.RenderTarget[0].SourceAlphaBlend = BlendOption.InverseSourceAlpha;
            blendStateDescription.RenderTarget[0].DestinationAlphaBlend = BlendOption.Zero;
            blendStateDescription.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
            blendStateDescription.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
            var blendState = new BlendState(D11Device, blendStateDescription);
            DeviceContext.OutputMerger.BlendFactor = Color.White;
            DeviceContext.OutputMerger.SetBlendState(blendState);

            //Depth
            var depthStencilStateDescription = new DepthStencilStateDescription
            {
                IsDepthEnabled = false,
                IsStencilEnabled = false,
                DepthWriteMask = DepthWriteMask.All,
                DepthComparison = Comparison.Always,
                FrontFace =
                {
                    FailOperation = StencilOperation.Keep,
                    DepthFailOperation = StencilOperation.Keep,
                    PassOperation = StencilOperation.Keep,
                    Comparison = Comparison.Always
                }
            };

            depthStencilStateDescription.BackFace = depthStencilStateDescription.FrontFace;
            var depthStencilState = new DepthStencilState(D11Device, depthStencilStateDescription);
            DeviceContext.OutputMerger.SetDepthStencilState(depthStencilState);

            _viewport = new Viewport
            {
                Height = _form.ClientSize.Height,
                Width = _form.ClientSize.Width,
                X = 0,
                Y = 0,
                MaxDepth = 1,
                MinDepth = 0
            };

            // Setup and create the viewport for rendering.
            DeviceContext.Rasterizer.SetViewport(_viewport);
            DeviceContext.OutputMerger.SetRenderTargets(RenderTargetView);

            DeviceContext.Rasterizer.State =
                new RasterizerState(D11Device, new RasterizerStateDescription {FillMode = FillMode.Solid, CullMode = CullMode.None});
        }

        public void Clear(Color4 clear)
        {
            _clearColor = clear;
            Clear();
        }

        public void Clear()
        {
            DeviceContext.ClearRenderTargetView(RenderTargetView, _clearColor);
        }

        public void DisposeTexture(string name)
        {
            lock (_sync)
            {
                if (LoadedTexturesByName.TryGetValue(name, out var texture))
                {
                    LoadedTexturesByPtrs.Remove(texture.NativePointer);
                    LoadedTexturesByName.Remove(name);
                    texture.Dispose();
                }
                else
                    DebugWindow.LogError($"({nameof(DisposeTexture)}) Texture {name} not found.", 10);
            }
        }

        public void AddOrUpdateTexture(string name, ShaderResourceView texture)
        {
            lock (_sync)
            {
                if (LoadedTexturesByName.TryGetValue(name, out var res)) res.Dispose();
                LoadedTexturesByName[name] = texture;
                LoadedTexturesByPtrs[texture.NativePointer] = texture;
            }
        }

        public ShaderResourceView GetTexture(string name)
        {
            if (LoadedTexturesByName.TryGetValue(name, out var result)) return result;
            throw new FileNotFoundException($"Texture by name: {name} not found");
        }

        public ShaderResourceView GetTexture(IntPtr ptr)
        {
            if (LoadedTexturesByPtrs.TryGetValue(ptr, out var result)) return result;
            throw new FileNotFoundException($"Texture by ptr: {ptr} not found");
        }

        public bool HasTexture(string name)
        {
            return LoadedTexturesByName.ContainsKey(name);
        }

        public bool HasTexture(IntPtr name)
        {
            return LoadedTexturesByPtrs.ContainsKey(name);
        }

        public void Render(double sleepTime, Core core)
        {
            try
            {
                Clear(Color.Transparent);
                ImGui.NewFrame();
                // ImGuiRender.InputUpdate();
                ImGuiRender.BeginBackGroundWindow();

                _debugTime = _sw.Elapsed.TotalMilliseconds;
                core.Tick();
                _coreTickDebug.Tick = _sw.Elapsed.TotalMilliseconds - _debugTime;

                _debugTime = _sw.Elapsed.TotalMilliseconds;
                SpritesRender.Render();
                _spritesDebug.Tick = _sw.Elapsed.TotalMilliseconds - _debugTime;

                _debugTime = _sw.Elapsed.TotalMilliseconds;
                ImGuiRender.Render();
                _imGuiDebug.Tick = _sw.Elapsed.TotalMilliseconds - _debugTime;

                _debugTime = _sw.Elapsed.TotalMilliseconds;
                _swapChain.Present(VSync ? 1 : 0, PresentFlags.None);
                _swapchainDebug.Tick = _sw.Elapsed.TotalMilliseconds - _debugTime;

                ImGui.GetIO().DeltaTime = (float) Time.DeltaTime;
            }
            catch (Exception e)
            {
                DebugWindow.LogError($"DX11.Render -> {e}");
            }
        }
    }
}
