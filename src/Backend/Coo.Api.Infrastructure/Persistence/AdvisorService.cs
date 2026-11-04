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

    /// <summary>A sleep plan's words: asked for only by apps that wait a minute (1.10 on).</summary>
    private static readonly TimeSpan PlanDeadline = TimeSpan.FromSeconds(40);

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
        var system = AdvisorPrompt.System(baby?.Name, weeks, request.Context, request.Language, TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone));

        var contents = AdvisorPrompt.History(request.History)
            .Select(m => (object)new { role = m.IsUser ? "user" : "model", parts = new[] { new { text = m.Text } } })
            .Append(new { role = "user", parts = new[] { new { text = AdvisorPrompt.Truncate(request.Question, AdvisorPrompt.MaxQuestionLength) } } })
            .ToArray();
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents,
            // Gemini 3 thinks by default, which made an answer take 13–24 s; an Advisor answer needs little of it.
            generationConfig = new { temperature = 0.4, thinkingConfig = new { thinkingLevel = "minimal" } },
        };

        // Premium's better model while the month's better answers last (AdvisorPrompt.PremiumBetterPerMonth).
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var better = isPremium
            && await db.AdvisorUsages.Where(u => u.UserId == user.Value && u.Date >= monthStart).SumAsync(u => u.BetterCount, cancellationToken)
                < AdvisorPrompt.PremiumBetterPerMonth;
        var models = better ? AdvisorPrompt.PremiumModels : AdvisorPrompt.Models;
        var (text, model) = await GenerateAsync(apiKey, body, models, AdvisorPrompt.Deadline(request.WaitSeconds), cancellationToken);
        if (text is null)
            return new AskAdvisorResponse(null, "advisor.unavailable");

        // Counted only once answered: a failure does not use up a question.
        usage.QuestionCount++;
        if (better && model == AdvisorPrompt.PremiumModels[0])
            usage.BetterCount++;
        await db.SaveChangesAsync(cancellationToken);
        return new AskAdvisorResponse(text);
    }

    public async Task<SleepPlanWordsResponse> WriteSleepPlanAsync(
        HouseholdId householdId, UserId user, SleepPlanWordsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await billingStore.IsHouseholdPremiumAsync(householdId, cancellationToken))
            return new SleepPlanWordsResponse(null, "premium_required");

        var apiKey = config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return new SleepPlanWordsResponse(null, "advisor.unavailable");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var usage = await db.AdvisorUsages.FirstOrDefaultAsync(u => u.UserId == user.Value && u.Date == today, cancellationToken);
        if (usage is null)
        {
            usage = new AdvisorUsageRecord { UserId = user.Value, Date = today, QuestionCount = 0 };
            db.AdvisorUsages.Add(usage);
        }

        if (usage.PlanCount >= SleepPlanPrompt.DailyTexts)
            return new SleepPlanWordsResponse(null, "advisor.daily-limit", SleepPlanPrompt.DailyTexts);

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SleepPlanPrompt.System(request.Kind, request.Language) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = SleepPlanPrompt.User(request.Facts) } } } },
            generationConfig = new { temperature = 0.3, thinkingConfig = new { thinkingLevel = "minimal" } },
        };
        // A reply that quotes a time the plan does not have is not shown: the phone keeps the stored texts.
        var text = SleepPlanPrompt.Accept((await GenerateAsync(apiKey, body, AdvisorPrompt.Models, PlanDeadline, cancellationToken)).Text, request.Facts);
        if (text is null)
            return new SleepPlanWordsResponse(null, "advisor.unavailable");

        usage.PlanCount++;
        await db.SaveChangesAsync(cancellationToken);
        return new SleepPlanWordsResponse(text);
    }

    /// <summary>
    /// Gemini's text for <paramref name="body"/>: each model in turn while one is busy, all within <see cref="Deadline"/>;
    /// null when there is none, which the phone shows as "the Advisor is unavailable, try again" rather than timing out.
    /// </summary>
    /// <returns>The text and the model that wrote it; (null, null) when none did in time.</returns>
    private static async Task<(string? Text, string? Model)> GenerateAsync(
        string apiKey, object body, IReadOnlyList<string> models, TimeSpan within, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(within);
        try
        {
            for (var attempt = 0; attempt < models.Count * 2; attempt++)
            {
                var model = models[attempt % models.Count];
                var uri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
                using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = System.Net.Http.Json.JsonContent.Create(body) };
                // In a header, not the URL, so the key never lands in a log line.
                message.Headers.Add("x-goog-api-key", apiKey);
                using var response = await Gemini.SendAsync(message, deadline.Token);
                if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                    continue;
                if (!response.IsSuccessStatusCode)
                    return (null, null);

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                return string.IsNullOrWhiteSpace(text) ? (null, null) : (text.Trim(), model);
            }

            return (null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException
                                       or TaskCanceledException { InnerException: TimeoutException })
        {
            return (null, null);
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

