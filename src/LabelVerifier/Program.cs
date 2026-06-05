using LabelVerifier.Endpoints;
using LabelVerifier.Engines;
using LabelVerifier.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureOpenAiOptions>(builder.Configuration.GetSection(AzureOpenAiOptions.SectionName));

builder.Services.AddHttpClient<AzureOpenAiLabelReader>();
builder.Services.AddSingleton<TesseractLabelReader>();
builder.Services.AddSingleton<FallbackLabelReader>();
builder.Services.AddSingleton<LabelVerificationService>();

// Allow large batch uploads (200-300 labels at once).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 512L * 1024 * 1024; // 512 MB
    o.ValueCountLimit = 5000;
    o.MultipartHeadersCountLimit = 5000;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapVerificationEndpoints();

app.Run();
