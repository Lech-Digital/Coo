using System;
using System.Linq;
using Google.Apis.AndroidPublisher.v3;

class Program {
    static void Main() {
        var type = typeof(PurchasesResource.SubscriptionsResource);
        foreach (var method in type.GetMethods()) {
            if (method.Name == "Get") {
                var p = string.Join(", ", method.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                Console.WriteLine($"Get({p})");
            }
        }
        var type2 = typeof(Google.Apis.Auth.OAuth2.CredentialFactory);
        foreach (var method in type2.GetMethods()) {
            if (method.Name.Contains("Json")) {
                var p = string.Join(", ", method.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                Console.WriteLine($"CredentialFactory.{method.Name}({p})");
            }
        }
    }
}
