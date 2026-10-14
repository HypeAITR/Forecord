using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenRecorder.Services.GpuEncoding;

/// <summary>
/// Scales a full-resolution D3D11 desktop texture (with cursor already
/// composited) into a target output resolution texture exclusively on the GPU
/// using a fullscreen triangle + linear sampler. Zero CPU readback.
/// </summary>
internal sealed class D3D11TextureScaler : IDisposable
{
    private const string ShaderSource = @"
struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

Texture2D SrcTex : register(t0);
SamplerState LinearSamp : register(s0);

float4 PS(VSOut i) : SV_Target
{
    return SrcTex.Sample(LinearSamp, i.uv);
}";

    private readonly Device _device;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;
    private readonly SamplerState _sampler;
    private readonly RasterizerState _rasterizer;
    private readonly BlendState _blendState;
    private readonly DepthStencilState _depthState;
    private readonly Texture2D _outputTexture;
    private readonly RenderTargetView _rtv;
    private readonly RawViewportF _viewport;
    private bool _disposed;

    public D3D11TextureScaler(Device device, int outWidth, int outHeight)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));

        using var vsCode = ShaderBytecode.Compile(ShaderSource, "VS", "vs_4_0", ShaderFlags.OptimizationLevel3);
        using var psCode = ShaderBytecode.Compile(ShaderSource, "PS", "ps_4_0", ShaderFlags.OptimizationLevel3);

        _vs = new VertexShader(device, vsCode);
        _ps = new PixelShader(device, psCode);

        _sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MinimumLod = 0,
            MaximumLod = float.MaxValue
        });

        _rasterizer = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = false,
            IsScissorEnabled = false
        });

        _blendState = new BlendState(device, BlendStateDescription.Default());
        _depthState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            IsStencilEnabled = false
        });

        _outputTexture = new Texture2D(device, new Texture2DDescription
        {
            Width = outWidth,
            Height = outHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });

        _rtv = new RenderTargetView(device, _outputTexture);
        _viewport = new RawViewportF
        {
            X = 0f,
            Y = 0f,
            Width = outWidth,
            Height = outHeight,
            MinDepth = 0f,
            MaxDepth = 1f
        };
    }

    public Texture2D OutputTexture => _outputTexture;

    public Texture2D Scale(Texture2D sourceTexture)
    {
        using var srv = new ShaderResourceView(_device, sourceTexture);
        var ctx = _device.ImmediateContext;

        ctx.InputAssembler.InputLayout = null;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(_vs);
        ctx.PixelShader.Set(_ps);
        ctx.PixelShader.SetSampler(0, _sampler);
        ctx.PixelShader.SetShaderResource(0, srv);
        ctx.Rasterizer.State = _rasterizer;
        ctx.Rasterizer.SetViewport(_viewport);
        ctx.OutputMerger.SetBlendState(_blendState);
        ctx.OutputMerger.SetDepthStencilState(_depthState);
        ctx.OutputMerger.SetRenderTargets(_rtv);

        ctx.Draw(3, 0);

        // Unbind SRV/RTV so sourceTexture can be freely updated on the next frame.
        ctx.PixelShader.SetShaderResource(0, null);
        ctx.OutputMerger.ResetTargets();

        return _outputTexture;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rtv.Dispose();
        _outputTexture.Dispose();
        _depthState.Dispose();
        _blendState.Dispose();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
