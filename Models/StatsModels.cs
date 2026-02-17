namespace DigCfoWebApi;

public record StatsSummary(
    int TotalCustomers,
    int ActiveCustomers,
    int TotalUsers,
    int ActiveUsers7d,
    int ActiveUsers30d,
    DateTimeOffset GeneratedAtUtc);

public record CustomerOverview(
    Guid AccountId,
    string CustomerName,
    long? OrganizationNumber,
    string? SubscriptionName,
    int UsersCount,
    DateTime? LastLoginUtc,
    string? PrimaryUserEmail,
    string? PrimaryUserName,
    int? LastSyncStatus,
    DateTime? LastSyncEndUtc,
    IReadOnlyList<CustomerUser> Users);

public record CustomerUser(
    Guid UserId,
    string? Email,
    string? FullName,
    DateTime? LastLoginUtc);
