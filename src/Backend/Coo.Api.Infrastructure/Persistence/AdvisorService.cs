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
    public async Task<AskAdvisorResponse> AskAsync(HouseholdId householdId, AskAdvisorRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Rate limiting
        var isPremium = await billingStore.IsHouseholdPremiumAsync(householdId, cancellationToken);
        var limit = isPremium ? 100 : 3; // 100 for premium, 3 for free

        // Find the user ID from the household members - just pick the first owner/parent for usage tracking
        var member = await db.Memberships.FirstOrDefaultAsync(m => m.HouseholdId == householdId.Value && m.RevokedAt == null, cancellationToken);
        var userId = member?.UserId ?? Guid.Empty;

        var today = timeProvider.GetLocalNow().Date;
        var dateOnly = DateOnly.FromDateTime(today);

        var usage = await db.AdvisorUsages.FirstOrDefaultAsync(u => u.UserId == userId && u.Date == dateOnly, cancellationToken);
        if (usage == null)
        {
            usage = new AdvisorUsageRecord { UserId = userId, Date = dateOnly, QuestionCount = 0 };
            db.AdvisorUsages.Add(usage);
        }

        if (usage.QuestionCount >= limit)
        {
            return new AskAdvisorResponse("You have reached your daily limit of questions. " + (isPremium ? "Please try again tomorrow." : "Upgrade to Premium for more questions!"));
        }

        usage.QuestionCount++;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            // Build context
            var baby = await db.BabyProfiles.FirstOrDefaultAsync(b => b.Id == request.BabyId && !b.IsDeleted, cancellationToken);

            var context = "You are an empathetic pediatric advisor. No medical diagnoses. You are advising the parents of a baby.";

            if (baby?.BirthDate != null)
            {
                var ageDays = (today - baby.BirthDate.Value.ToDateTime(TimeOnly.MinValue)).TotalDays;
                context += $" The baby is {Math.Floor(ageDays / 7)} weeks old.";
            }

            if (isPremium)
            {
                // Inject recent logs
                var logs = await db.CareEntries
                    .Where(e => e.HouseholdId == householdId.Value && e.SubjectBabyId == request.BabyId && !e.IsDeleted)
                    .OrderByDescending(e => e.StartAt)
                    .Take(20)
                    .ToListAsync(cancellationToken);

                if (logs.Count > 0)
                {
                    context += "\nHere are the recent care logs:\n";
                    foreach (var log in logs)
                    {
                        context += $"- {log.StartAt:g}: {log.Kind}\n";
                    }
                }
            }

            var prompt = $"{context}\n\nPlease answer in plain text without any markdown formatting (no asterisks for bold, no hashes for headers, no bullet points - use simple dashes if needed). Keep it conversational.\n\nQuestion: {request.Question}";

            var apiKey = config["Gemini:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                return new AskAdvisorResponse("Advisor is currently unavailable (missing configuration).");
            }

            using var http = new HttpClient();
            var contents = new List<object>();

            if (request.History != null)
            {
                foreach (var msg in request.History)
                {
                    contents.Add(new
                    {
                        role = msg.IsUser ? "user" : "model",
                        parts = new[] { new { text = msg.Text } }
                    });
                }
            }

            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = prompt } }
            });

            var requestObj = new
            {
                contents = contents.ToArray(),
                generationConfig = new { temperature = 0.7 }
            };

            var requestContent = System.Net.Http.Json.JsonContent.Create(requestObj);
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-lite-latest:generateContent?key={apiKey}";

            HttpResponseMessage? httpResponse = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(requestObj)
                };

                httpResponse = await http.SendAsync(httpRequest, cancellationToken);
                if (httpResponse.IsSuccessStatusCode)
                    break;

                var isTransient = httpResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                                  httpResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests;

                if (!isTransient || attempt == 3)
                    break;

                await Task.Delay(400 * attempt, cancellationToken);
            }

            if (httpResponse == null || !httpResponse.IsSuccessStatusCode)
            {
                var err = httpResponse != null ? await httpResponse.Content.ReadAsStringAsync(cancellationToken) : "Unknown error";
                var code = httpResponse?.StatusCode.ToString() ?? "Unknown";
                return new AskAdvisorResponse($"Advisor API Error: {code} - {err}");
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);

            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return new AskAdvisorResponse(text ?? "I'm not sure how to answer that.");
        }
        catch (Exception ex)
        {
            return new AskAdvisorResponse($"Sorry, I couldn't process your request right now. ({ex.Message})");
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
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                        {
                            var text = textProp.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                var svgStart = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
                                var svgEnd = text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
                                if (svgStart >= 0 && svgEnd > svgStart)
                                {
                                    var svgXml = text.Substring(svgStart, svgEnd + 6 - svgStart);
                                    var pngBytes = RenderSvgToPng(svgXml, 512, 512);
                                    if (pngBytes != null && pngBytes.Length > 0)
                                    {
                                        return new GenerateAvatarResponse(Convert.ToBase64String(pngBytes), null);
                                    }
                                }
                            }
                        }
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

    private static byte[]? RenderSvgToPng(string svgXml, int width, int height)
    {
        try
        {
            using var svg = new SKSvg();
            svg.FromSvg(svgXml);
            if (svg.Picture == null)
                return null;

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null)
                return null;

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
            return data.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

