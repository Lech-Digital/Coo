using SkiaSharp;
using Svg.Skia;
using Xunit;

namespace Coo.Api.Infrastructure.Tests.Advisor;

public class AdvisorAvatarTests
{
    [Fact]
    public void RenderSvgToPng_ValidSvg_ProducesNonEmptyPngBytes()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="100%" height="100%">
              <circle cx="100" cy="100" r="90" fill="#f7f3ee" stroke="#e3dbd0" stroke-width="4" />
              <circle cx="100" cy="105" r="48" fill="#FCE4D6" />
              <circle cx="78" cy="114" r="14" fill="#F8CBAD" opacity="0.6" />
              <circle cx="122" cy="114" r="14" fill="#F8CBAD" opacity="0.6" />
            </svg>
            """;

        using var skSvg = new SKSvg();
        skSvg.FromSvg(svg);
        Assert.NotNull(skSvg.Picture);

        var info = new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        Assert.NotNull(surface);

        using var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var bounds = skSvg.Picture.CullRect;
        var scale = Math.Min(512f / bounds.Width, 512f / bounds.Height);
        var matrix = SKMatrix.CreateScale(scale, scale);
        canvas.DrawPicture(skSvg.Picture, in matrix);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();

        Assert.NotEmpty(bytes);
        // PNG magic header
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }
}
