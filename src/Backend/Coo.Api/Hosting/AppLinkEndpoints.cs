using System.Net;
using System.Text.Json.Serialization;
using Coo.Domain.Households;

namespace Coo.Api.Hosting;

internal sealed record AssetLinkTarget(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("package_name")] string PackageName,
    [property: JsonPropertyName("sha256_cert_fingerprints")] IReadOnlyList<string> Fingerprints);

internal sealed record AssetLinkStatement(
    [property: JsonPropertyName("relation")] IReadOnlyList<string> Relation,
    [property: JsonPropertyName("target")] AssetLinkTarget Target);

/// <summary>
/// The Android App Link's half on the server (Spec 011b §4.2–§4.3): <c>assetlinks.json</c>, and the page a browser shows when
/// the link does not open Coo. The page never looks the code up: it checks its shape and prints it, names nobody, and
/// says nothing about validity, so it is no oracle. English only for now (R1; seven languages in Increment 18).
/// </summary>
internal static class AppLinkEndpoints
{
    public const string PackageName = "com.lechdigital.coo";

    public static void MapAppLinkEndpoints(this IEndpointRouteBuilder app, IReadOnlyList<string> fingerprints)
    {
        AssetLinkStatement[] statements =
        [
            new(["delegate_permission/common.handle_all_urls"], new AssetLinkTarget("android_app", PackageName, fingerprints)),
        ];
        app.MapGet("/.well-known/assetlinks.json", () => Results.Json(statements, contentType: "application/json"));

        app.MapGet("/i/{**code}", (HttpContext http, string? code) =>
        {
            var headers = http.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Robots-Tag"] = "noindex";
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
            var parsed = InvitationCode.Parse(code);
            return Results.Content(Page(parsed.IsSuccess ? parsed.Value.Display : null), "text/html; charset=utf-8");
        });
    }

    internal static string Page(string? code)
    {
        var step2 = code is null
            ? "Open Coo, go to <strong>Settings → Family → Join a family</strong>, and enter the code from your invitation."
            : $"Open Coo, go to <strong>Settings → Family → Join a family</strong>, and enter this code: <strong class=\"code\">{WebUtility.HtmlEncode(code)}</strong>";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>Coo — Baby Log &amp; Sleep</title>
            <style>
            body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: #F1EDE5; color: #2B2620; margin: 0; padding: 32px 20px; line-height: 1.5; }
            main { max-width: 34rem; margin: 0 auto; }
            h1 { font-size: 1.5rem; margin: 0 0 12px; }
            ol { padding-left: 1.25rem; }
            li { margin: 10px 0; }
            .code { font-family: ui-monospace, monospace; font-size: 1.15rem; letter-spacing: 0.08em; }
            a { color: #8A4B22; }
            footer { margin-top: 32px; font-size: 0.9rem; color: #6B6258; }
            </style>
            </head>
            <body>
            <main>
            <h1>Coo — Baby Log &amp; Sleep</h1>
            <p>You've been invited to share a baby's log on Coo, a baby tracker for parents.</p>
            <ol>
            <li>Install Coo from <a href="https://play.google.com/store/apps/details?id={{PackageName}}">Google Play</a>.</li>
            <li>{{step2}}</li>
            </ol>
            <p>The code can be used once, within 72 hours of being sent. You'll need a Google account to join.</p>
            <footer><a href="https://lechdigital.nl/projects/coo/privacy">Privacy policy</a> · <a href="https://lechdigital.nl">lechdigital.nl</a></footer>
            </main>
            </body>
            </html>
            """;
    }
}
