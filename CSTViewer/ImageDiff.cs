using System.Diagnostics;
using System.IO;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer;

internal readonly record struct DiffProgress(int Percent, string Stage);

/// <summary>Python版と同じ画素比較で、全ページの差分をLZW圧縮のBigTIFFへ保存する。</summary>
internal static class ImageDiff
{
    public static Task<string> CreateAsync(string oldPath, string newPath, string outputPath,
        IProgress<DiffProgress>? progress = null, CancellationToken token = default) => Task.Run(async () =>
    {
        DiffSavePath.Validate(oldPath, newPath, outputPath);
        token.ThrowIfCancellationRequested();
        progress?.Report(new(0, "Preparing diff images"));
        // 表示と同様、操作キャッシュによるファイルの保持を避ける。
        ImageLoader.InitializeVips();
        int oldCount = PageCount(oldPath), newCount = PageCount(newPath);
        int pages = Math.Max(oldCount, newCount);
        string target = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".CSTViewer-diff-{Guid.NewGuid():N}.tif");
        try
        {
            using (var writer = new DiffTiffWriter(temporary))
            // 保存先フォルダーへの一時TIFFの公開を避け、同じ排他ハンドルで全ページを処理する。
            using (var page = new FileStream(Path.Combine(Path.GetTempPath(), $".CSTViewer-diff-page-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, ViewerSettings.DiffCopyBufferSize,
                FileOptions.DeleteOnClose))
            {
                for (int index = 0; index < pages; index++)
                {
                    token.ThrowIfCancellationRequested();
                    page.Position = 0;
                    page.SetLength(0);
                    int start = 5 + 90 * index / pages;
                    int end = 5 + 90 * (index + 1) / pages;
                    string stage = $"Creating diff TIFF ({index + 1}/{pages})";
                    progress?.Report(new(start, stage));
                    using (var result = CreatePage(oldPath, newPath, index, oldCount, newCount, token))
                    {
                        // libvipsの評価進捗を使い、大きな1ページの書き込み中も状態を更新する。
                        result.SetProgress(new InlineProgress<int>(percent =>
                            progress?.Report(new(start + (end - start) * percent / 100, stage))), token);
                        result.TiffsaveStream(page, compression: ViewerSettings.DiffTiffCompression,
                            bigtiff: true, keep: Enums.ForeignKeep.None, pageHeight: result.Height);
                    }
                    token.ThrowIfCancellationRequested();
                    writer.Append(page, token);
                    progress?.Report(new(end, stage));
                }
            }
            // 完成するまで既存の保存先を変更せず、読み込み中の共有違反だけを短時間リトライする。
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(96, "Finalizing the saved file"));
                try { File.Move(temporary, target, overwrite: true); break; }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33
                    && attempt < ViewerSettings.DiffReplaceRetryCount)
                {
                    progress?.Report(new(96, "Waiting for the output file to become available"));
                    await Task.Delay(ViewerSettings.DiffReplaceRetryMilliseconds, token);
                }
            }
            progress?.Report(new(100, "Diff file saved"));
            return target;
        }
        catch (VipsException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally
        {
            // 後始末の共有違反で、本来のエラーや保存結果を上書きしない。
            TryDeleteTemporaryFile(temporary);
        }
    }, token);

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete the temporary diff file: {path} ({exception.Message})");
        }
    }

    private static int PageCount(string path)
    {
        using var page = LoadPage(path, 0);
        return page.GetTypeOf("n-pages") != 0 ? Math.Max(1, (int)page.Get("n-pages")) : 1;
    }

    private static VipsImage LoadPage(string path, int index)
    {
        using var source = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tif" or ".tiff" => VipsImage.Tiffload(path, page: index, n: 1, autorotate: true,
                access: Enums.Access.Sequential, revalidate: true),
            ".gif" => VipsImage.Gifload(path, page: index, n: 1, access: Enums.Access.Sequential, revalidate: true),
            ".jpg" or ".jpeg" => VipsImage.Jpegload(path, autorotate: true, access: Enums.Access.Sequential, revalidate: true),
            _ => VipsImage.NewFromFile(path, access: Enums.Access.Sequential, revalidate: true)
        };
        return Normalize(source);
    }

    internal static VipsImage Normalize(VipsImage source)
    {
        VipsImage image;
        try { image = source.Interpretation == Enums.Interpretation.Srgb ? source.Copy() : source.Colourspace(Enums.Interpretation.Srgb); }
        catch (VipsException) { image = source.Copy(); }
        using (image)
        using (var opaque = image.HasAlpha() ? image.Flatten(background: [255, 255, 255]) : image.Copy())
        using (var gray = opaque.ExtractBand(0))
        using (var rgb = opaque.Bands < 3 ? gray.Bandjoin([gray, gray]) : opaque.ExtractBand(0, n: 3))
        using (var bytes = rgb.Cast(Enums.BandFormat.Uchar))
            return bytes.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static VipsImage CreatePage(string oldPath, string newPath, int index, int oldCount, int newCount,
        CancellationToken token)
    {
        using var oldPage = index < oldCount ? LoadPage(oldPath, index) : null;
        using var newPage = index < newCount ? LoadPage(newPath, index) : null;
        int width = Math.Max(oldPage?.Width ?? 1, newPage?.Width ?? 1);
        int height = Math.Max(oldPage?.Height ?? 1, newPage?.Height ?? 1);
        bool identical = false;
        if (oldPage is not null && newPage is not null && oldPage.Width == newPage.Width && oldPage.Height == newPage.Height)
        {
            using var unequal = oldPage.Relational(newPage, Enums.OperationRelational.Noteq);
            using var changed = unequal.Bandbool(Enums.OperationBoolean.Or);
            changed.SetProgress(new InlineProgress<int>(_ => { }), token);
            identical = changed.Max() == 0;
        }
        token.ThrowIfCancellationRequested();
        // 順次読み込みは一致判定で消費されるため、出力用に先頭から開き直す。
        using var oldAgain = index < oldCount ? LoadPage(oldPath, index) : null;
        using var newAgain = index < newCount ? LoadPage(newPath, index) : null;
        if (identical) return AddNoChangeText(newAgain!);
        using var oldCanvas = Canvas(oldAgain, width, height);
        using var newCanvas = Canvas(newAgain, width, height);
        return Compare(oldCanvas, newCanvas);
    }

    private static VipsImage Canvas(VipsImage? page, int width, int height)
    {
        if (page is not null) return page.Embed(0, 0, width, height, extend: Enums.Extend.Background, background: [255, 255, 255]);
        using var black = VipsImage.Black(width, height, bands: 3);
        using var white = black.NewFromImage([255, 255, 255]);
        return white.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    internal static VipsImage Compare(VipsImage oldImage, VipsImage newImage)
    {
        using var unequal = oldImage.Relational(newImage, Enums.OperationRelational.Noteq);
        using var changed = unequal.Bandbool(Enums.OperationBoolean.Or);
        using var oldInk = 255 - oldImage;
        using var newInk = 255 - newImage;
        using var oldMean = oldInk.Bandmean();
        using var newMean = newInk.Bandmean();
        using var darker = newMean >= oldMean;
        using var lighter = newMean < oldMean;
        using var added = changed & darker;
        using var deleted = changed & lighter;
        using var deletion = deleted.Ifthenelse(ViewerSettings.DiffDeletedColor, newImage);
        using var result = added.Ifthenelse(ViewerSettings.DiffAddedColor, deletion);
        using var bytes = result.Cast(Enums.BandFormat.Uchar);
        return bytes.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static VipsImage AddNoChangeText(VipsImage image)
    {
        int size = Math.Max(1, (int)Math.Round(image.Height * ViewerSettings.DiffNoChangeFontSizeRatio));
        using var text = VipsImage.Text(ViewerSettings.DiffNoChangeText,
            font: $"{ViewerSettings.DiffNoChangeFontFamily} {size}", dpi: 72, width: image.Width, align: Enums.Align.Centre);
        using var mask = text.Gravity(Enums.CompassDirection.Centre, image.Width, image.Height, extend: Enums.Extend.Black);
        using var result = mask.Ifthenelse(ViewerSettings.DiffAddedColor, image, blend: true);
        using var bytes = result.Cast(Enums.BandFormat.Uchar);
        return bytes.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
