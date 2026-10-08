using System;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AskAdvisorResponse))]
internal partial class AdvisorJsonContext : JsonSerializerContext {}

public class AskAdvisorResponse
{
    public string? Answer { get; set; }
}

class Program
{
    static void Main()
    {
        var json = "{\"Answer\":\"test\",\"answer\":\"test\"}";
        try {
            var answer = JsonSerializer.Deserialize(json, AdvisorJsonContext.Default.AskAdvisorResponse);
            Console.WriteLine("Parsed: " + (answer?.Answer ?? "null"));
        } catch (Exception ex) {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }
}
