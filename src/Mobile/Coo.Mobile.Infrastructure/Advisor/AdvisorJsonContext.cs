using System.Text.Json.Serialization;
using Coo.Mobile.Application.Advisor;

namespace Coo.Mobile.Infrastructure.Advisor;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AskAdvisorRequest))]
[JsonSerializable(typeof(AskAdvisorResponse))]
[JsonSerializable(typeof(AdvisorMessageHistory))]
[JsonSerializable(typeof(List<AdvisorMessageHistory>))]
internal partial class AdvisorJsonContext : JsonSerializerContext
{
}
