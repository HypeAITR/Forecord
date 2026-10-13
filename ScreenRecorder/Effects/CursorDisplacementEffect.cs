using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SharpDX.D3DCompiler;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;

namespace ScreenRecorder.Effects;

public sealed class CursorDisplacementEffect : ShaderEffect
{
    private const string HlslSource = @"
sampler2D input : register(s0);
float2 center   : register(c0);
float  strength : register(c1);
float  radius   : register(c2);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 delta = uv - center;
    float dist = length(delta);
    float n = saturate(dist / radius);
    float fall = 1.0 - n;
    fall = fall * fall;

    float h1 = frac(sin(dot(uv, float2(127.1, 311.7))) * 43758.5453);
    float h2 = frac(sin(dot(uv, float2(269.5, 183.3))) * 43758.5453);

    float2 jitter = float2(h1 - 0.5, h2 - 0.5) * 2.0;
    float2 dir = delta / max(dist, 0.00001);
    float2 offset = (jitter + dir * 0.15) * fall * strength;

    float4 color = tex2D(input, uv + offset);

    float grain = (h1 + h2) - 1.0;
    color.rgb += grain * fall * strength * 14.0;

    return color;
}";

    private static readonly PixelShader Shader = CompileShader();

    private static PixelShader CompileShader()
    {
        var result = ShaderBytecode.Compile(
            HlslSource,
            "main",
            "ps_2_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);

        if (result.HasErrors || result.Bytecode == null)
            throw new InvalidOperationException("Shader compile failed: " + result.Message);

        var bytes = result.Bytecode.Data;
        var shader = new PixelShader();
        shader.SetStreamSource(new MemoryStream(bytes, writable: false));
        return shader;
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(CursorDisplacementEffect), 0);

    public static readonly DependencyProperty CenterProperty =
        DependencyProperty.Register(
            nameof(Center),
            typeof(WpfPoint),
            typeof(CursorDisplacementEffect),
            new UIPropertyMetadata(new WpfPoint(0.5, 0.5), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty StrengthProperty =
        DependencyProperty.Register(
            nameof(Strength),
            typeof(double),
            typeof(CursorDisplacementEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(
            nameof(Radius),
            typeof(double),
            typeof(CursorDisplacementEffect),
            new UIPropertyMetadata(0.16, PixelShaderConstantCallback(2)));

    public CursorDisplacementEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(CenterProperty);
        UpdateShaderValue(StrengthProperty);
        UpdateShaderValue(RadiusProperty);
    }

    public WpfBrush Input
    {
        get => (WpfBrush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public WpfPoint Center
    {
        get => (WpfPoint)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public double Strength
    {
        get => (double)GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }
}
