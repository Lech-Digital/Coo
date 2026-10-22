using System.Text.Json;
using Coo.Api.Application.Advisor;
using Coo.Api.Application.Billing;
using Coo.Api.Application.Sync;
using Coo.Api.Infrastructure.Persistence;
using Coo.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using Svg.Skia;

namespace Coo.Api.Infrastructure.Persistence;

internal sealed class AdvisorService(
    IDbContextFactory<CooDbContext> dbFactory,
    IBillingStore billingStore,
    TimeProvider timeProvider,
    IConfiguration config) : IAdvisorService
{
    // One client for the process: a new HttpClient per question exhausts sockets under load.
    private static readonly HttpClient Gemini = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Uri AskUri = new("https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-lite-latest:generateContent");

    public async Task<AskAdvisorResponse> AskAsync(HouseholdId householdId, UserId user, AskAdvisorRequest request, CancellationToken cancellationToken)
    {
        var apiKey = config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return new AskAdvisorResponse(null, "advisor.unavailable");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Each member has their own daily allowance; the day is the family's, not the server's.
        var isPremium = await billingStore.IsHouseholdPremiumAsync(householdId, cancellationToken);
        var limit = isPremium ? AdvisorPrompt.PremiumDailyQuestions : AdvisorPrompt.FreeDailyQuestions;
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var usage = await db.AdvisorUsages.FirstOrDefaultAsync(u => u.UserId == user.Value && u.Date == today, cancellationToken);
        if (usage is null)
        {
            usage = new AdvisorUsageRecord { UserId = user.Value, Date = today, QuestionCount = 0 };
            db.AdvisorUsages.Add(usage);
        }

        if (usage.QuestionCount >= limit)
            return new AskAdvisorResponse(null, isPremium ? "advisor.daily-limit" : "advisor.free-limit", limit);

        var baby = await db.BabyProfiles.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == request.BabyId && b.HouseholdId == householdId.Value && !b.IsDeleted, cancellationToken);
        int? weeks = baby?.BirthDate is { } born ? (today.DayNumber - born.DayNumber) / 7 : null;
        var system = AdvisorPrompt.System(baby?.Name, weeks, request.Context, request.Language);

        var contents = AdvisorPrompt.History(request.History)
            .Select(m => (object)new { role = m.IsUser ? "user" : "model", parts = new[] { new { text = m.Text } } })
            .Append(new { role = "user", parts = new[] { new { text = AdvisorPrompt.Truncate(request.Question, AdvisorPrompt.MaxQuestionLength) } } })
            .ToArray();
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents,
            generationConfig = new { temperature = 0.4 },
        };

        try
        {
            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                response?.Dispose();
                using var message = new HttpRequestMessage(HttpMethod.Post, AskUri) { Content = System.Net.Http.Json.JsonContent.Create(body) };
                // In a header, not the URL, so the key never lands in a log line.
                message.Headers.Add("x-goog-api-key", apiKey);
                response = await Gemini.SendAsync(message, cancellationToken);
                if (response.IsSuccessStatusCode
                    || response.StatusCode is not (System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                    || attempt == 3)
                    break;
                await Task.Delay(400 * attempt, cancellationToken);
            }

            using (response)
            {
                if (response is null || !response.IsSuccessStatusCode)
                    return new AskAdvisorResponse(null, "advisor.unavailable");

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(text))
                    return new AskAdvisorResponse(null, "advisor.unavailable");

                // Counted only once answered: a failure does not use up a question.
                usage.QuestionCount++;
                await db.SaveChangesAsync(cancellationToken);
                return new AskAdvisorResponse(text.Trim());
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException
                                       or TaskCanceledException { InnerException: TimeoutException })
        {
            return new AskAdvisorResponse(null, "advisor.unavailable");
        }
    }

    public async Task<GenerateAvatarResponse> GenerateAvatarAsync(HouseholdId householdId, GenerateAvatarRequest request, CancellationToken cancellationToken)
    {
        var isPremium = await billingStore.IsHouseholdPremiumAsync(householdId, cancellationToken);
        if (!isPremium)
        {
            return new GenerateAvatarResponse(null, "premium_required");
        }

        var apiKey = config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            return new GenerateAvatarResponse(null, "missing_configuration");
        }

        try
        {
            using var http = new HttpClient();
            var prompt = """
                You are an expert artist specializing in storybook baby illustrations.
                Look closely at this baby photo. Analyze the baby's distinctive features:
                - Skin tone and complexion
                - Hair color and hairstyle (e.g. wispy swirl, soft curls, bald/fine hair, ponytail)
                - Face shape and cute chubby cheeks
                - Eye shape, eye color, and happy gentle expression
                - Clothing color or collar
                
                Generate a beautiful, warm, minimalist circular SVG avatar illustration reflecting this specific baby.
                Artistic requirements:
                - Circular avatar composition with a soft pastel circular background (e.g. warm cream #FAF6F0 or soft sage #E8EFE9).
                - Use cute rounded shapes, chubby cheeks, soft gentle smile, friendly eyes.
                - Clean vector paths with solid fills and subtle outlines.
                - The avatar must be framed within viewBox="0 0 200 200".
                - Return ONLY the raw valid <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="100%" height="100%">...</svg> XML string.
                - Do NOT include any explanations, markdown code blocks, or backticks. Return ONLY the <svg>...</svg> tag.
                """;

            var cleanBase64 = request.ImageBase64;
            var commaIdx = cleanBase64.IndexOf(',');
            if (commaIdx >= 0)
            {
                cleanBase64 = cleanBase64[(commaIdx + 1)..];
            }

            var requestObj = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = "image/jpeg",
                                    data = cleanBase64
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2
                }
            };

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-lite-latest:generateContent?key={apiKey}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = System.Net.Http.Json.JsonContent.Create(requestObj)
            };

            var httpResponse = await http.SendAsync(httpRequest, cancellationToken);
            if (!httpResponse.IsSuccessStatusCode)
            {
                var err = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                return new GenerateAvatarResponse(null, $"Gemini API error: {httpResponse.StatusCode} - {err}");
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() > 0)
            {
                var content = candidates[0].GetProperty("content");
                if (content.TryGetProperty("parts", out var parts))
                {
                    var fullText = new System.Text.StringBuilder();
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                        {
                            fullText.Append(textProp.GetString());
                        }
                    }

                    var text = fullText.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var svgStart = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
                        var svgEnd = text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
                        if (svgStart >= 0 && svgEnd > svgStart)
                        {
                            var svgXml = text.Substring(svgStart, svgEnd + 6 - svgStart);
                            var (pngBytes, renderError) = RenderSvgToPng(svgXml, 512, 512);
                            if (pngBytes != null && pngBytes.Length > 0)
                            {
                                return new GenerateAvatarResponse(Convert.ToBase64String(pngBytes), null);
                            }
                            return new GenerateAvatarResponse(null, $"Rasterization failed: {renderError}");
                        }

                        var snippet = text.Length > 120 ? text[..120] + "..." : text;
                        return new GenerateAvatarResponse(null, $"No <svg> tag in response: {snippet}");
                    }
                }
            }

            return new GenerateAvatarResponse(null, "Could not extract SVG illustration from AI response.");
        }
        catch (Exception ex)
        {
            return new GenerateAvatarResponse(null, ex.Message);
        }
    }

    private static (byte[]? Bytes, string? Error) RenderSvgToPng(string svgXml, int width, int height)
    {
        try
        {
            using var svg = new SKSvg();
            svg.FromSvg(svgXml);
            if (svg.Picture == null)
                return (null, "SKSvg.Picture was null after FromSvg.");

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null)
                return (null, "SKSurface.Create returned null.");

            using var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var bounds = svg.Picture.CullRect;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                var scale = Math.Min((float)width / bounds.Width, (float)height / bounds.Height);
                var matrix = SKMatrix.CreateScale(scale, scale);
                canvas.DrawPicture(svg.Picture, in matrix);
            }
            else
            {
                canvas.DrawPicture(svg.Picture);
            }

            canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return (data.ToArray(), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}

