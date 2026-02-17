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
    bool IsDeleted,
    bool IsDisabled,
    bool? IsActive,
    int? RegistrationStatusId,
    string? RegistrationStatus,
    int? LastSyncStatus,
    DateTime? LastSyncEndUtc,
    IReadOnlyList<CustomerUser> Users);

public record CustomerUser(
    Guid? UserId,
    string? Email,
    string? FullName,
    DateTime? LastLoginUtc,
    IReadOnlyList<string> Roles);

public record OrganizationLookupResult(
    Guid AccountId,
    string? CustomerName,
    long OrganizationNumber,
    bool? IsArchived,
    bool? IsActive);

public record DeletedCustomerFlagSummary(
    bool? IsArchived,
    bool? IsActive,
    int? RegistrationStatusId,
    string? RegistrationStatus,
    int Count);
