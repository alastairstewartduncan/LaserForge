using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using AsdLasercraft.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AsdLasercraft.App.Services;

/// <summary>
/// The on-PC background removal model: U²-Net (u2netp, 4.6 MB, Apache-2.0, via the rembg project). It's downloaded
/// once on first use and checked against a fixed SHA-256, then runs offline with ONNX Runtime on the CPU.
/// </summary>
public sealed class BackgroundModel : ISaliencyModel, IDisposable
{
    public const string FileName = "u2netp.onnx";
    public const string DownloadUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";
    public const string Sha256 = "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8";
    public const long SizeBytes = 4_574_861;

    public static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD Lasercraft", "models", FileName);

    public static bool IsDownloaded => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == SizeBytes;

    private static BackgroundModel? _shared;
    private readonly InferenceSession _session;
    private readonly string _inputName;

    public int InputSize => 320;

    private BackgroundModel(string path)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(path, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>Loads the model (once per run of the app). Call <see cref="DownloadAsync"/> first if needed.</summary>
    public static BackgroundModel Load() => _shared ??= new BackgroundModel(ModelPath);

    public float[] Run(float[] input)
    {
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, InputSize, InputSize });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        // The first output is the fused, full-resolution saliency map.
        return results.First().AsEnumerable<float>().ToArray();
    }

    /// <summary>Downloads the model, verifying its checksum before it is used.</summary>
    public static async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var temp = ModelPath + ".download";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AsdLasercraft/" + AsdLasercraft.Core.AppInfo.Version);
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? SizeBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(temp);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        string hash;
        await using (var fs = File.OpenRead(temp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (hash != Sha256)
        {
            File.Delete(temp);
            throw new InvalidDataException("The downloaded model didn't match its expected checksum, so it wasn't used. Please try again later.");
        }
        File.Move(temp, ModelPath, overwrite: true);
    }

    public void Dispose() => _session.Dispose();
}
