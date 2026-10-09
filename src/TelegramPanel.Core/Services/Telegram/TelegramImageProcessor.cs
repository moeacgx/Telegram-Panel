using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Telegram 图片处理工具。
/// </summary>
public static class TelegramImageProcessor
{
    public static async Task<MemoryStream> PrepareAvatarJpegAsync(Stream fileStream, CancellationToken cancellationToken = default)
    {
        if (fileStream == null)
            throw new ArgumentNullException(nameof(fileStream));

        await using var raw = new MemoryStream();
        if (fileStream.CanSeek)
            fileStream.Position = 0;

        await fileStream.CopyToAsync(raw, cancellationToken);
        raw.Position = 0;

        RejectTiffHeader(raw);
        using var image = await Image.LoadAsync(raw, cancellationToken);
        image.Mutate(x => x.AutoOrient());
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Crop,
            Size = new Size(512, 512)
        }));

        var encoded = new MemoryStream();
        await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, cancellationToken);
        encoded.Position = 0;
        return encoded;
    }

    public static async Task<MemoryStream> PrepareStoredImageJpegAsync(
        Stream fileStream,
        int maxDimension = 2048,
        CancellationToken cancellationToken = default)
    {
        if (fileStream == null)
            throw new ArgumentNullException(nameof(fileStream));

        if (maxDimension < 256)
            maxDimension = 256;

        await using var raw = new MemoryStream();
        if (fileStream.CanSeek)
            fileStream.Position = 0;

        await fileStream.CopyToAsync(raw, cancellationToken);
        raw.Position = 0;

        RejectTiffHeader(raw);
        using var image = await Image.LoadAsync(raw, cancellationToken);
        image.Mutate(x => x.AutoOrient());

        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxDimension, maxDimension)
            }));
        }

        var encoded = new MemoryStream();
        await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 88 }, cancellationToken);
        encoded.Position = 0;
        return encoded;
    }

    private static void RejectTiffHeader(MemoryStream raw)
    {
        // 必须在图片解码器运行前按内容拒绝，不能信任文件名或 MIME。
        var header = raw.GetBuffer().AsSpan(0, (int)Math.Min(raw.Length, 4));
        if (header.Length == 4
            && ((header[0] == 0x49 && header[1] == 0x49 && header[3] == 0
                    && header[2] is 0x2A or 0x2B)
                || (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0
                    && header[3] is 0x2A or 0x2B)))
        {
            throw new InvalidOperationException("暂不支持 TIFF/BigTIFF 图片，请先转换为 JPEG、PNG 或 WebP 后重试。");
        }
    }
}
