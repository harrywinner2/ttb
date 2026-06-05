using System.Collections.Concurrent;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using LabelVerifier.Models;
using LabelVerifier.Services;

namespace LabelVerifier.Endpoints;

public static class VerificationEndpoints
{
    private static readonly string[] AllowedTypes =
        { "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif", "image/bmp" };

    // Keep the batch from overwhelming Azure OpenAI rate limits while staying fast.
    private const int MaxConcurrency = 6;

    public static void MapVerificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", (LabelVerificationService svc) => Results.Ok(new
        {
            status = "ok",
            primaryEngine = svc.Reader.PrimaryName,
            primaryAvailable = svc.Reader.PrimaryAvailable,
            fallbackEngine = svc.Reader.FallbackName,
            fallbackAvailable = svc.Reader.FallbackAvailable
        }));

        app.MapPost("/api/verify", async (HttpRequest request, LabelVerificationService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Send the label as multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("image");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Please attach a label image." });

            var typeError = ValidateImage(file);
            if (typeError is not null)
                return Results.BadRequest(new { error = typeError });

            var app2 = new LabelApplication
            {
                FileName = file.FileName,
                BrandName = form["brandName"],
                ClassType = form["classType"],
                AlcoholContent = form["alcoholContent"],
                NetContents = form["netContents"],
                BottlerNameAddress = form["bottlerNameAddress"],
                CountryOfOrigin = form["countryOfOrigin"],
            };
            var forceFallback = form["engine"].ToString().Equals("offline", StringComparison.OrdinalIgnoreCase);

            var bytes = await ToBytesAsync(file, ct);
            var result = await svc.VerifyAsync(bytes, file.ContentType, app2, forceFallback, ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/verify/batch", async (HttpRequest request, LabelVerificationService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Send the labels as multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            var images = form.Files.Where(f => f.Name == "images" && f.Length > 0).ToList();
            if (images.Count == 0)
                return Results.BadRequest(new { error = "Please attach at least one label image." });

            var forceFallback = form["engine"].ToString().Equals("offline", StringComparison.OrdinalIgnoreCase);

            // Optional CSV manifest mapping filename -> expected application fields.
            var manifest = new Dictionary<string, LabelApplication>(StringComparer.OrdinalIgnoreCase);
            var manifestFile = form.Files.GetFile("manifest");
            if (manifestFile is not null && manifestFile.Length > 0)
                manifest = await ParseManifestAsync(manifestFile, ct);

            // Pre-read bytes (file streams aren't safe to read concurrently).
            var jobs = new List<(string name, byte[] bytes, string contentType, LabelApplication app)>();
            foreach (var f in images)
            {
                if (ValidateImage(f) is not null) continue;
                var app2 = manifest.TryGetValue(f.FileName, out var a) ? a : new LabelApplication();
                app2.FileName = f.FileName;
                jobs.Add((f.FileName, await ToBytesAsync(f, ct), f.ContentType, app2));
            }

            var results = new ConcurrentBag<VerificationResult>();
            using var gate = new SemaphoreSlim(MaxConcurrency);
            await Task.WhenAll(jobs.Select(async job =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    results.Add(await svc.VerifyAsync(job.bytes, job.contentType, job.app, forceFallback, ct));
                }
                catch (Exception ex)
                {
                    results.Add(new VerificationResult
                    {
                        FileName = job.name,
                        Overall = CheckStatus.Fail,
                        Error = "Could not process this image: " + ex.Message
                    });
                }
                finally { gate.Release(); }
            }));

            var ordered = results.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(new
            {
                total = ordered.Count,
                pass = ordered.Count(r => r.Overall == CheckStatus.Pass),
                review = ordered.Count(r => r.Overall == CheckStatus.Review),
                fail = ordered.Count(r => r.Overall == CheckStatus.Fail),
                results = ordered
            });
        });
    }

    private static string? ValidateImage(IFormFile file)
    {
        if (!AllowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return $"“{file.FileName}” is not a supported image type (use JPG, PNG, or WEBP).";
        if (file.Length > 25L * 1024 * 1024)
            return $"“{file.FileName}” is larger than 25 MB.";
        return null;
    }

    private static async Task<byte[]> ToBytesAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task<Dictionary<string, LabelApplication>> ParseManifestAsync(IFormFile file, CancellationToken ct)
    {
        var map = new Dictionary<string, LabelApplication>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(file.OpenReadStream());
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = a => a.Header.Trim().ToLowerInvariant().Replace("_", "").Replace(" ", ""),
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(reader, cfg);
        await csv.ReadAsync();
        csv.ReadHeader();
        while (await csv.ReadAsync())
        {
            var name = Field(csv, "filename", "file", "image");
            if (string.IsNullOrWhiteSpace(name)) continue;
            map[name.Trim()] = new LabelApplication
            {
                FileName = name.Trim(),
                BrandName = Field(csv, "brandname", "brand"),
                ClassType = Field(csv, "classtype", "class", "type"),
                AlcoholContent = Field(csv, "alcoholcontent", "abv", "alcohol"),
                NetContents = Field(csv, "netcontents", "volume", "size"),
                BottlerNameAddress = Field(csv, "bottlernameaddress", "bottler", "producer", "nameaddress"),
                CountryOfOrigin = Field(csv, "countryoforigin", "country", "origin"),
            };
        }
        return map;
    }

    private static string? Field(CsvReader csv, params string[] names)
    {
        foreach (var n in names)
            if (csv.TryGetField<string>(n, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}
