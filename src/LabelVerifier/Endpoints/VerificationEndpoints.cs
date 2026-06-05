using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using LabelVerifier.Models;
using LabelVerifier.Services;

namespace LabelVerifier.Endpoints;

public static class VerificationEndpoints
{
    private static readonly string[] AllowedTypes =
        { "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif", "image/bmp" };

    private const int MaxImagesPerProduct = 4; // front, back, neck, gift box

    public static void MapVerificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", (LabelVerificationService svc, BatchProcessor batch) => Results.Ok(new
        {
            status = "ok",
            primaryEngine = svc.Reader.PrimaryName,
            primaryAvailable = svc.Reader.PrimaryAvailable,
            fallbackEngine = svc.Reader.FallbackName,
            fallbackAvailable = svc.Reader.FallbackAvailable,
            batchParallelism = batch.MaxParallelism
        }));

        // ---- Single product (one or more images of the same product) ----
        app.MapPost("/api/verify", async (HttpRequest request, LabelVerificationService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Send the label as multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            var files = form.Files.Where(f => (f.Name == "image" || f.Name == "images") && f.Length > 0).ToList();
            if (files.Count == 0)
                return Results.BadRequest(new { error = "Please attach a label image." });

            var product = new ProductInput
            {
                App = new LabelApplication
                {
                    FileName = files[0].FileName,
                    BrandName = form["brandName"],
                    ClassType = form["classType"],
                    AlcoholContent = form["alcoholContent"],
                    NetContents = form["netContents"],
                    BottlerNameAddress = form["bottlerNameAddress"],
                    CountryOfOrigin = form["countryOfOrigin"],
                }
            };
            foreach (var f in files.Take(MaxImagesPerProduct))
            {
                if (ValidateImage(f) is { } err) return Results.BadRequest(new { error = err });
                product.Images.Add(new ImageBlob(await ToBytesAsync(f, ct), f.ContentType));
            }

            var forceFallback = IsOffline(form["engine"]);
            var result = await svc.VerifyProductAsync(product.Images, product.App, forceFallback, ct);
            return Results.Ok(result);
        });

        // ---- Async batch: returns a job id immediately; products are processed in parallel ----
        app.MapPost("/api/batch/jobs", async (HttpRequest request, BatchJobStore store, BatchProcessor processor, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Send the labels as multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            var (products, error) = await BuildProductsAsync(form, ct);
            if (error is not null) return Results.BadRequest(new { error });
            if (products.Count == 0) return Results.BadRequest(new { error = "Please attach at least one label image." });

            var job = store.Create(products.Count);
            processor.Start(job, products, IsOffline(form["engine"]));
            return Results.Accepted($"/api/batch/jobs/{job.Id}", new { jobId = job.Id, total = job.Total });
        });

        app.MapGet("/api/batch/jobs/{id}", (string id, BatchJobStore store) =>
        {
            var job = store.Get(id);
            if (job is null) return Results.NotFound(new { error = "Job not found or expired." });

            var results = job.Bag.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(new
            {
                jobId = job.Id,
                status = job.Status.ToString().ToLowerInvariant(),
                total = job.Total,
                completed = job.Completed,
                pass = results.Count(r => r.Overall == CheckStatus.Pass),
                review = results.Count(r => r.Overall == CheckStatus.Review),
                fail = results.Count(r => r.Overall == CheckStatus.Fail),
                results
            });
        });

        // ---- Synchronous batch (kept for back-compat / scripts; one image per file) ----
        app.MapPost("/api/verify/batch", async (HttpRequest request, LabelVerificationService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Send the labels as multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            var (products, error) = await BuildProductsAsync(form, ct);
            if (error is not null) return Results.BadRequest(new { error });
            if (products.Count == 0) return Results.BadRequest(new { error = "Please attach at least one label image." });

            var forceFallback = IsOffline(form["engine"]);
            var bag = new ConcurrentBag<VerificationResult>();
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(products.Select(async p =>
            {
                await gate.WaitAsync(ct);
                try { bag.Add(await svc.VerifyProductAsync(p.Images, p.App, forceFallback, ct)); }
                finally { gate.Release(); }
            }));

            var ordered = bag.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
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

    // ---- Build the list of products from uploaded files + an optional CSV/JSON manifest ----
    private static async Task<(List<ProductInput> products, string? error)> BuildProductsAsync(
        IFormCollection form, CancellationToken ct)
    {
        var images = form.Files.Where(f => (f.Name == "images" || f.Name == "image") && f.Length > 0).ToList();
        var byName = new Dictionary<string, ImageBlob>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in images)
        {
            if (ValidateImage(f) is { } err) return (new(), err);
            byName[f.FileName] = new ImageBlob(await ToBytesAsync(f, ct), f.ContentType);
        }

        var manifestFile = form.Files.GetFile("manifest");
        var products = new List<ProductInput>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (manifestFile is { Length: > 0 })
        {
            using var reader = new StreamReader(manifestFile.OpenReadStream());
            var text = await reader.ReadToEndAsync(ct);
            var trimmed = text.TrimStart();
            var isJson = manifestFile.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                         || manifestFile.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || trimmed.StartsWith('[') || trimmed.StartsWith('{');

            var specs = isJson ? ParseJsonManifest(text) : ParseCsvManifest(text);
            foreach (var spec in specs)
            {
                var product = new ProductInput { App = spec.App };
                foreach (var name in spec.Images.Take(MaxImagesPerProduct))
                    if (byName.TryGetValue(name, out var blob)) { product.Images.Add(blob); consumed.Add(name); }
                if (product.Images.Count > 0)
                {
                    product.App.FileName ??= spec.Images.FirstOrDefault();
                    products.Add(product);
                }
            }
        }

        // Any uploaded image not claimed by the manifest becomes its own single-image product.
        foreach (var f in images.Where(f => !consumed.Contains(f.FileName)))
        {
            var p = new ProductInput { App = new LabelApplication { FileName = f.FileName } };
            p.Images.Add(byName[f.FileName]);
            products.Add(p);
        }

        return (products, null);
    }

    // ---- Manifest spec (expected fields + the image filenames for this product) ----
    private sealed record ProductSpec(LabelApplication App, List<string> Images);

    private static List<ProductSpec> ParseJsonManifest(string json)
    {
        var list = new List<ProductSpec>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : new List<JsonElement> { root };
        foreach (var el in items)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string? S(params string[] keys)
            {
                foreach (var k in keys)
                    foreach (var prop in el.EnumerateObject())
                        if (string.Equals(prop.Name.Replace("_", "").Replace(" ", ""), k, StringComparison.OrdinalIgnoreCase)
                            && prop.Value.ValueKind == JsonValueKind.String)
                            return prop.Value.GetString();
                return null;
            }
            var imgs = new List<string>();
            foreach (var prop in el.EnumerateObject())
                if (prop.Name.Replace("_", "").Equals("images", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array)
                    imgs.AddRange(prop.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                else if (prop.Name.Replace("_", "").Equals("image", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                    imgs.Add(prop.Value.GetString()!);

            var app = new LabelApplication
            {
                FileName = S("product", "name") ?? imgs.FirstOrDefault(),
                BrandName = S("brandname", "brand"),
                ClassType = S("classtype", "class", "type"),
                AlcoholContent = S("alcoholcontent", "abv", "alcohol"),
                NetContents = S("netcontents", "volume", "size"),
                BottlerNameAddress = S("bottlernameaddress", "bottler", "producer"),
                CountryOfOrigin = S("countryoforigin", "country", "origin"),
            };
            if (imgs.Count > 0) list.Add(new ProductSpec(app, imgs));
        }
        return list;
    }

    private static List<ProductSpec> ParseCsvManifest(string csvText)
    {
        var list = new List<ProductSpec>();
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = a => a.Header.Trim().ToLowerInvariant().Replace("_", "").Replace(" ", ""),
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(new StringReader(csvText), cfg);
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
        {
            string? F(params string[] names)
            {
                foreach (var n in names)
                    if (csv.TryGetField<string>(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
                return null;
            }
            var name = F("filename", "file", "image");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var app = new LabelApplication
            {
                FileName = name.Trim(),
                BrandName = F("brandname", "brand"),
                ClassType = F("classtype", "class", "type"),
                AlcoholContent = F("alcoholcontent", "abv", "alcohol"),
                NetContents = F("netcontents", "volume", "size"),
                BottlerNameAddress = F("bottlernameaddress", "bottler", "producer", "nameaddress"),
                CountryOfOrigin = F("countryoforigin", "country", "origin"),
            };
            list.Add(new ProductSpec(app, new List<string> { name.Trim() }));
        }
        return list;
    }

    private static bool IsOffline(string? engine) =>
        string.Equals(engine, "offline", StringComparison.OrdinalIgnoreCase);

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
}
