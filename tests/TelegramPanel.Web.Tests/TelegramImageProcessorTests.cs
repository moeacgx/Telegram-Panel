using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Web.Api;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramImageProcessorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 正常未压缩TIFF也在解码前被拒绝(bool avatar)
    {
        // 只使用正常的未压缩小图，不运行畸形 CCITT 或超大目录计数夹具。
        using var source = new Image<Rgba32>(2, 2, Color.Green);
        await using var input = new MemoryStream();
        await source.SaveAsync(input, new TiffEncoder { Compression = TiffCompression.None });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync(input, avatar));
        Assert.Contains("TIFF/BigTIFF", error.Message);
        Assert.Contains("JPEG、PNG 或 WebP", error.Message);
    }

    [Theory]
    [InlineData(true, "49492A0008000000")]
    [InlineData(false, "49492A0008000000")]
    [InlineData(true, "4D4D002A00000008")]
    [InlineData(false, "4D4D002A00000008")]
    [InlineData(true, "49492B0008000000")]
    [InlineData(false, "49492B0008000000")]
    [InlineData(true, "4D4D002B00080000")]
    [InlineData(false, "4D4D002B00080000")]
    public async Task 两个入口按真实头部拒绝大小端TIFF和BigTIFF(bool avatar, string header)
    {
        await using var input = new MemoryStream(Convert.FromHexString(header));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync(input, avatar));
        Assert.Contains("TIFF/BigTIFF", error.Message);
    }

    [Theory]
    [InlineData(true, "jpeg")]
    [InlineData(false, "jpeg")]
    [InlineData(true, "png")]
    [InlineData(false, "png")]
    [InlineData(true, "webp")]
    [InlineData(false, "webp")]
    public async Task 常用格式仍转换为JPEG(bool avatar, string format)
    {
        using var source = new Image<Rgba32>(40, 20, Color.Green);
        await using var input = new MemoryStream();
        IImageEncoder encoder = format switch
        {
            "jpeg" => new JpegEncoder(),
            "png" => new PngEncoder(),
            _ => new WebpEncoder()
        };
        await source.SaveAsync(input, encoder);

        await using var output = await PrepareAsync(input, avatar);
        Assert.Equal(0, output.Position);
        Assert.Equal(JpegFormat.Instance, await Image.DetectFormatAsync(output));
        output.Position = 0;
        using var decoded = await Image.LoadAsync(output);
        Assert.Equal(avatar ? 512 : 40, decoded.Width);
        Assert.Equal(avatar ? 512 : 20, decoded.Height);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JPEG方向信息仍在缩放前生效(bool avatar)
    {
        using var source = new Image<Rgba32>(80, 40);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
            source[x, y] = x < 40 ? Color.Red : Color.Blue;
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        await using var input = new MemoryStream();
        await source.SaveAsJpegAsync(input, new JpegEncoder { Quality = 100 });

        await using var output = await PrepareAsync(input, avatar);
        using var decoded = await Image.LoadAsync<Rgba32>(output);
        Assert.Equal(avatar ? 512 : 40, decoded.Width);
        Assert.Equal(avatar ? 512 : 80, decoded.Height);
        var top = decoded[decoded.Width / 2, decoded.Height / 4];
        var bottom = decoded[decoded.Width / 2, decoded.Height * 3 / 4];
        Assert.True(top.R > top.B + 100);
        Assert.True(bottom.B > bottom.R + 100);
    }

    private static Task<MemoryStream> PrepareAsync(Stream stream, bool avatar) => avatar
        ? TelegramImageProcessor.PrepareAvatarJpegAsync(stream)
        : TelegramImageProcessor.PrepareStoredImageJpegAsync(stream);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 任务头像拒绝伪装为JPEG的TIFF且正常PNG仍可保存(bool tiff)
    {
        var root = Path.Combine(Path.GetTempPath(), $"telegram-panel-image-test-{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:RootPath"] = root }).Build();
        var storage = new ImageAssetStorageService(config, new TestEnvironment(),
            NullLogger<ImageAssetStorageService>.Instance);
        await using var input = new MemoryStream();
        if (tiff)
            await input.WriteAsync(Convert.FromHexString("49492A0008000000"));
        else
        {
            using var image = new Image<Rgba32>(2, 2, Color.Green);
            await image.SaveAsPngAsync(input);
        }
        input.Position = 0;
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection
            {
                new FormFile(input, 0, input.Length, "file", "avatar.jpg")
                {
                    Headers = new HeaderDictionary(), ContentType = "image/jpeg"
                }
            });
        try
        {
            var result = await PanelAdminApiEndpoints.UploadTaskAvatarAssetAsync(
                http.Request, storage, CancellationToken.None);
            Assert.Equal(tiff ? 400 : 200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            if (tiff)
            {
                var error = Assert.IsType<OperationResultDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
                Assert.False(error.Success);
                Assert.Contains("TIFF/BigTIFF", error.Message);
                Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            }
            else
            {
                Assert.IsType<TaskAssetUploadResultDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
                var saved = Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
                Assert.Equal(JpegFormat.Instance, await Image.DetectFormatAsync(saved));
            }
        }
        finally
        {
            // root 是本测试独占的 GUID 子目录，清理前核对仍位于临时根目录。
            Assert.True(StoragePathResolver.IsPathWithin(root, Path.GetTempPath()));
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
