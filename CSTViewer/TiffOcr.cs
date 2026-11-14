using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace CSTViewer;

internal static class TiffOcr
{
    public static ProcessStartInfo CreateStartInfo()
    {
        string? configured = Environment.GetEnvironmentVariable(OcrSearchSettings.TesseractEnvironmentVariable);
        string directory = string.IsNullOrWhiteSpace(configured) ? OcrSearchSettings.DefaultTesseractDirectory : configured;
        string executable = Path.Combine(directory, "tesseract.exe");
        string tessdata = Path.Combine(directory, "tessdata");
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Tesseract was not found. Install it or set {OcrSearchSettings.TesseractEnvironmentVariable} to its folder.\n{executable}");
        foreach (string model in OcrSearchSettings.Languages.Split('+').Append("osd"))
        {
            string modelPath = Path.Combine(tessdata, $"{model}.traineddata");
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Tesseract language data is missing.\n{modelPath}");
        }
        string tsvConfig = Path.Combine(tessdata, "configs", "tsv");
        if (!File.Exists(tsvConfig))
            throw new FileNotFoundException($"Tesseract TSV configuration is missing.\n{tsvConfig}");

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[]
        {
            "stdin", "stdout", "--tessdata-dir", tessdata, "-l", OcrSearchSettings.Languages,
            "--oem", OcrSearchSettings.EngineMode, "--psm", OcrSearchSettings.PageSegmentationMode, "tsv"
        })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public static async Task<OcrSearchPage[]> RecognizeSearchPagesAsync(string filePath,
        ProcessStartInfo startInfo, IReadOnlyList<Size> pageSizes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(filePath);
        using var process = Process.Start(startInfo) ?? throw new IOException("Could not start Tesseract.");
        // パイプへの書き込み待ちでも、子プロセスを止めてキャンセルを進める。
        using var registration = token.Register(() =>
        {
            try { StopProcess(process); }
            catch (Win32Exception exception) { Debug.WriteLine(exception); }
        });
        // 入力の転送と並行して両方の出力を読み、パイプが満杯になるのを防ぐ。
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> diagnostics = process.StandardError.ReadToEndAsync();
        try
        {
            IOException? inputError = null;
            try
            {
                using var standardInput = process.StandardInput;
                await input.CopyToAsync(standardInput.BaseStream, token).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // 読み込みの途中でTesseractが終了した場合も、標準エラーから原因を取得する。
                inputError = exception;
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string errors = await diagnostics.ConfigureAwait(false);
            string tsv = await output.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // OSDの警告は許容する。画像・モデルの読み込みエラーは終了コード0でも失敗とする。
            if (process.ExitCode != 0 || inputError is not null ||
                errors.Contains("Error in pixRead", StringComparison.OrdinalIgnoreCase) ||
                errors.Contains("Error during processing", StringComparison.OrdinalIgnoreCase) ||
                errors.Contains("Error opening data file", StringComparison.OrdinalIgnoreCase) ||
                errors.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase))
            {
                string message = string.IsNullOrWhiteSpace(errors) ? inputError?.Message ?? $"Exit code {process.ExitCode}." : errors.Trim();
                throw new InvalidDataException($"Tesseract could not read or recognize the TIFF.\n{message}", inputError);
            }
            return ParseTsv(tsv, pageSizes, token);
        }
        finally
        {
            // キャンセル時もプロセスが終了してから一時TIFFを片付ける。
            StopProcess(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, diagnostics).ConfigureAwait(false);
        }
    }

    internal static OcrSearchPage[] ParseTsv(string tsv, IReadOnlyList<Size> pageSizes, CancellationToken token = default)
    {
        var pages = new List<OcrSearchPage>();
        List<IReadOnlyList<OcrWordRegion>>? lines = null;
        List<OcrWordRegion>? words = null;
        using var reader = new StringReader(tsv);
        // TSVの先頭行は列名。階層1の行は単語がないページにも出力される。
        reader.ReadLine();
        while (reader.ReadLine() is { } row)
        {
            token.ThrowIfCancellationRequested();
            if (row.Length == 0) continue;
            string[] fields = row.Split('\t', 12);
            if (fields.Length != 12 || !TryReadNumber(fields[1], out int pageNumber))
                throw new InvalidDataException("Tesseract returned an invalid TSV row.");
            if (fields[0] == "1")
            {
                if (pageNumber != pages.Count + 1 || pageNumber > pageSizes.Count)
                    throw new InvalidDataException("Tesseract returned the TIFF pages out of order.");
                Size size = pageSizes[pageNumber - 1];
                if (!TryReadNumber(fields[8], out int width) || width != size.Width ||
                    !TryReadNumber(fields[9], out int height) || height != size.Height)
                    throw new InvalidDataException($"Tesseract returned unexpected dimensions for page {pageNumber}.");
                lines = [];
                words = null;
                pages.Add(new OcrSearchPage(lines, size));
                continue;
            }
            if (lines is null || pageNumber != pages.Count)
                throw new InvalidDataException("Tesseract returned a row for an unexpected TIFF page.");
            if (fields[0] == "4")
            {
                words = [];
                lines.Add(words);
            }
            if (fields[0] == "5" && !string.IsNullOrWhiteSpace(fields[11]))
            {
                if (words is null || !TryReadNumber(fields[6], out int left) || !TryReadNumber(fields[7], out int top) ||
                    !TryReadNumber(fields[8], out int width) || !TryReadNumber(fields[9], out int height) ||
                    width <= 0 || height <= 0)
                    throw new InvalidDataException("Tesseract returned an invalid word region.");
                Size size = pages[^1].PixelSize;
                var bounds = Rect.Intersect(new Rect(left, top, width, height), new Rect(size));
                if (bounds.Width > 0 && bounds.Height > 0)
                    words.Add(new OcrWordRegion(fields[11].Trim(), bounds));
            }
        }
        if (pages.Count != pageSizes.Count)
            throw new InvalidDataException($"Tesseract returned {pages.Count} of {pageSizes.Count} TIFF pages.");
        return pages.ToArray();
    }

    private static bool TryReadNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

    private static void StopProcess(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (Win32Exception) when (process.HasExited) { }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}
