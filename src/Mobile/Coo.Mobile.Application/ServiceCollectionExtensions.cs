using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Application;

/// <summary>Registers the use cases. The host calls it from the composition root (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMobileApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<LogEntry>();
        services.AddTransient<ReviseEntry>();
        services.AddTransient<ResubmitRejectedEntry>();
        services.AddTransient<DeleteEntry>();
        services.AddTransient<GetEntries>();
        services.AddTransient<EnsureProvisionalHousehold>();
        services.AddTransient<GetToday>();
        services.AddTransient<QuickActions>();
        services.AddTransient<AiLogging.TranscribeVoice>();
        services.AddTransient<Import.ImportLog>();
        services.AddTransient<Import.ExportLog>();
        services.AddSingleton<ILogAccess, Households.FamilyLogAccess>();
        services.AddTransient<Insights.GetSleepPrediction>();
        services.AddTransient<Insights.GetSleepPlan>();
        services.AddTransient<Insights.PreviewSleepPlan>();
        services.AddTransient<Insights.StartSleepPlan>();
        services.AddTransient<Insights.AnswerSleepCheckIn>();
        services.AddTransient<Insights.StopSleepPlan>();
        services.AddTransient<Growth.GetGrowth>();
        services.AddTransient<Reports.BuildCareReport>();
        services.AddTransient<GetDay>();
        services.AddTransient<GetCareTrends>();
        services.AddTransient<GetDailyAverages>();
        services.AddTransient<Advisor.BuildAdvisorContext>();
        services.AddTransient<Demo.SeedDemoLog>();
        services.AddTransient<AiLogging.PrepareQuickLog>();
        services.AddTransient<AiLogging.ApplyQuickLog>();
        services.AddTransient<SetUpBaby>();
        services.AddTransient<RecordBirth>();
        services.AddTransient<UpdateBabyProfile>();
        services.AddTransient<SetBabySex>();
        services.AddTransient<ChangeVolumeUnit>();
        services.AddTransient<SetHiddenKinds>();
        services.AddTransient<GetAttention>();
        services.AddTransient<DismissDuplicate>();
        services.AddTransient<MergeFeeds>();
        services.AddTransient<UndoFeedMerge>();
        services.AddTransient<AutoResolveAttention>();

        // Sign-in and sessions (Spec 010 §6).
        services.AddSingleton<Account>();
        services.AddSingleton<Billing.EntitlementRefresher>();
        services.AddTransient<SignIn>();
        services.AddTransient<SignOut>();
        services.AddTransient<DeleteAccount>();
        services.AddTransient<RefreshAccount>();

        // Sync (Spec 011).
        services.AddSingleton<LocalWrites>();
        services.AddTransient<ConsentGate>();
        services.AddTransient<SyncCycle>();
        services.AddSingleton<SyncStops>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ISyncStatus>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddSingleton<ISyncControl>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddTransient<RecordConsent>();
        services.AddTransient<DeclineConsent>();
        services.AddTransient<BringEntriesOver>();

        // Members and invitations (Spec 011b).
        services.AddTransient<ProvisionalSettings>();
        services.AddSingleton<Families>();
        services.AddTransient<SignOutOfPhone>();
        services.AddTransient<BringFamilyEntriesOver>();
        services.AddTransient<JoinFamily>();
        services.AddSingleton<LoadFamilyMembers>();
        services.AddTransient<InviteParent>();
        services.AddTransient<WithdrawFamilyInvitation>();
        services.AddTransient<RemoveFamilyMember>();
        services.AddTransient<LeaveFamily>();
        return services;
    }
}
