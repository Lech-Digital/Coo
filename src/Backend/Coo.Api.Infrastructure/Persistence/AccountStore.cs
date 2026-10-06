using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Users through ASP.NET Core Identity (ADR 0003 §3), households and memberships (active rows only, Spec 011b §10.3).
/// Scoped: it shares the request's <see cref="CooDbContext"/> with Identity's user store, so the user, their login, the household and the membership
/// are written in one transaction. Identity's <see cref="UserManager{TUser}"/> takes no cancellation token, so the
/// token is checked before each of its calls; everything else passes it down.
/// </summary>
internal sealed class AccountStore(CooDbContext context, UserManager<CooUser> users) : IAccountStore
{
    public const string GoogleProvider = "Google";

    /// <summary>Unique per Google account (Identity's <c>UserNameIndex</c> is unique), never shared like an email.</summary>
    public static string UserName(string googleSubject) => "google:" + googleSubject;

    public async Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .Where(u => context.UserLogins.Any(l => l.UserId == u.Id && l.LoginProvider == GoogleProvider && l.ProviderKey == subject))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : ToAccount(user);
    }

    public async Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id.Value, cancellationToken);
        return user is null ? null : ToAccount(user);
    }

    public Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken) =>
        context.Households.AnyAsync(h => h.Id == id.Value, cancellationToken);

    /// <summary>
    /// Two first sign-ins for the same Google account can race: both found no account. The user name (and the login's
    /// key) is unique, so the database refuses the second; its transaction rolls back whole and this throws, leaving no
    /// user without a login. The person's retry then finds the account.
    /// </summary>
    public async Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var user = new CooUser
            {
                Id = account.UserId.Value,
                UserName = UserName(account.GoogleSubject),
                Email = account.Email,
                EmailConfirmed = true,
                DisplayName = account.DisplayName,
            };
            cancellationToken.ThrowIfCancellationRequested();
            Check(await users.CreateAsync(user));
            cancellationToken.ThrowIfCancellationRequested();
            Check(await users.AddLoginAsync(user, new UserLoginInfo(GoogleProvider, account.GoogleSubject, GoogleProvider)));
            AddHousehold(account.Household, account.UserId, account.CreatedAt);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            context.ChangeTracker.Clear();
            throw new InvalidOperationException("An account for this Google account already exists; nothing was created.", e);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AddHousehold(household, owner, now);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByIdAsync(account.Id.Value.ToString("D", CultureInfo.InvariantCulture));
        if (user is null)
            return;
        user.Email = account.Email;
        user.DisplayName = account.DisplayName;
        cancellationToken.ThrowIfCancellationRequested();
        Check(await users.UpdateAsync(user));
    }

    public async Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        [.. (await context.Memberships.AsNoTracking().Where(m => m.UserId == user.Value && m.RevokedAt == null).ToListAsync(cancellationToken)).Select(ToMembership)];

    public async Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        [.. (await context.Memberships.AsNoTracking().Where(m => m.HouseholdId == household.Value && m.RevokedAt == null).ToListAsync(cancellationToken)).Select(ToMembership)];

    private void AddHousehold(HouseholdId household, UserId owner, DateTimeOffset now)
    {
        context.Households.Add(new HouseholdRecord { Id = household.Value, CreatedAt = now.UtcDateTime, CreatedBy = owner.Value });
        context.Memberships.Add(new MembershipRecord
        {
            HouseholdId = household.Value,
            UserId = owner.Value,
            Role = RoleText.Owner,
            JoinedAt = now.UtcDateTime,
        });
    }

    private static UserAccount ToAccount(CooUser user) => new(new UserId(user.Id), user.Email ?? "", user.DisplayName);

    private static Membership ToMembership(MembershipRecord row) => Membership.Create(
        new HouseholdId(row.HouseholdId), new UserId(row.UserId), RoleText.Parse(row.Role), ServerColumns.Utc(row.JoinedAt));

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Identity refused the account: " + string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
