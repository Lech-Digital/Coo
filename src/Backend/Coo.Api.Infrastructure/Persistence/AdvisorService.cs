using System.Text.Json;
using Coo.Api.Application.Advisor;
using Coo.Api.Application.Billing;
using Coo.Api.Application.Sync;
using Coo.Api.Infrastructure.Persistence;
using Coo.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
}
