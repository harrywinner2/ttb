using System.Threading.RateLimiting;
using LabelVerifier.Endpoints;
using LabelVerifier.Engines;
using LabelVerifier.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureOpenAiOptions>(builder.Configuration.GetSection(AzureOpenAiOptions.SectionName));

builder.Services.AddHttpClient<AzureOpenAiLabelReader>();
builder.Services.AddSingleton<TesseractLabelReader>();
builder.Services.AddSingleton<FallbackLabelReader>();
builder.Services.AddSingleton<LabelVerificationService>();
builder.Services.AddSingleton<BatchJobStore>();
builder.Services.AddSingleton<BatchProcessor>();

// Allow large batch uploads (200-300 labels, up to 4 images each).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1024L * 1024 * 1024; // 1 GB
    o.ValueCountLimit = 20000;
    o.MultipartHeadersCountLimit = 20000;
});

// Container Apps terminates TLS and forwards the client IP — honour it so rate limiting
// partitions by the real caller rather than the ingress.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// Per-IP rate limit to protect a (costly) AI endpoint from abuse.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 240,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 20,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});

var app = builder.Build();

app.UseForwardedHeaders();

// Baseline security headers on every response.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

app.UseRateLimiter();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapVerificationEndpoints();

app.Run();
