using Microsoft.Data.SqlClient;

namespace DigCfoWebApi;

public sealed class StatsRepository
{
    private readonly IConfiguration _configuration;

    public StatsRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<StatsSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(GetConnectionString("capassa-registration-db"));
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM Registration_Account WHERE ISNULL(IsArchived, 0) = 0) AS TotalCustomers,
                (SELECT COUNT(*) FROM Registration_Account WHERE ISNULL(IsArchived, 0) = 0 AND IsActive = 1) AS ActiveCustomers,
                (SELECT COUNT(*) FROM Registration_User WHERE IsArchived = 0) AS TotalUsers,
                (SELECT COUNT(DISTINCT RegistrationUserId) FROM User_Login_History WHERE Date >= DATEADD(day, -7, GETUTCDATE())) AS ActiveUsers7d,
                (SELECT COUNT(DISTINCT RegistrationUserId) FROM User_Login_History WHERE Date >= DATEADD(day, -30, GETUTCDATE())) AS ActiveUsers30d;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No summary data returned.");
        }

        return new StatsSummary(
            TotalCustomers: reader.GetInt32(0),
            ActiveCustomers: reader.GetInt32(1),
            TotalUsers: reader.GetInt32(2),
            ActiveUsers7d: reader.GetInt32(3),
            ActiveUsers30d: reader.GetInt32(4),
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<CustomerOverview>> GetCustomersAsync(CancellationToken cancellationToken)
    {
        var customers = new List<CustomerOverview>();

        var customerSql = """
            WITH AccountUsers AS (
                SELECT AccountId, UserId, CAST(NULL AS nvarchar(256)) AS RoleEmail FROM Registration_Account_User_Role
                UNION ALL
                SELECT AccountId, UserId, Email AS RoleEmail FROM Capassa_Account_User_Role
            ),
            ActiveSub AS (
                SELECT
                    ash.AccountId,
                    ash.SbscriptionId,
                    ROW_NUMBER() OVER (
                        PARTITION BY ash.AccountId
                        ORDER BY ash.IsActive DESC, ash.EndDate DESC, ash.StartDate DESC
                    ) AS rn
                FROM Account_Subscription_History ash
            ),
            SubName AS (
                SELECT
                    st.SbscriptionId,
                    st.Name,
                    ROW_NUMBER() OVER (
                        PARTITION BY st.SbscriptionId
                        ORDER BY CASE WHEN st.LanguageId IN ('nb', 'no', 'en') THEN 0 ELSE 1 END, st.LanguageId
                    ) AS rn
                FROM Capassa_Subscription_Text st
            ),
            UserAgg AS (
                SELECT
                    au.AccountId,
                    COUNT(DISTINCT COALESCE(CONVERT(nvarchar(36), au.UserId), LOWER(COALESCE(u.Email, au.RoleEmail)))) AS UsersCount,
                    MAX(ulh.Date) AS LastLoginUtc
                FROM AccountUsers au
                LEFT JOIN Registration_User u ON u.Id = au.UserId
                LEFT JOIN User_Login_History ulh ON ulh.RegistrationUserId = au.UserId
                WHERE COALESCE(CONVERT(nvarchar(36), au.UserId), LOWER(COALESCE(u.Email, au.RoleEmail))) IS NOT NULL
                  AND (u.Id IS NULL OR ISNULL(u.IsArchived, 0) = 0)
                GROUP BY au.AccountId
            ),
            PrimaryUser AS (
                SELECT
                    a.Id AS AccountId,
                    u.Email AS PrimaryUserEmail,
                    CONCAT(u.First_Name, ' ', u.Last_Name) AS PrimaryUserName
                FROM Registration_Account a
                LEFT JOIN Registration_User u ON u.Id = a.Primary_User_Id
            )
            SELECT
                a.Id,
                a.Name,
                a.OrganizationNumber,
                sn.Name AS SubscriptionName,
                COALESCE(ua.UsersCount, 0) AS UsersCount,
                ua.LastLoginUtc,
                pu.PrimaryUserEmail,
                pu.PrimaryUserName,
                CAST(ISNULL(a.IsArchived, 0) AS bit) AS IsDeleted,
                CAST(CASE
                    WHEN ISNULL(a.IsActive, 1) = 0 THEN 1
                    WHEN UPPER(ISNULL(ras.Status, '')) = 'DISABLED' THEN 1
                    ELSE 0
                END AS bit) AS IsDisabled,
                a.IsActive,
                a.RegistrationStatusId,
                ras.Status AS RegistrationStatus
            FROM Registration_Account a
            LEFT JOIN ActiveSub s ON s.AccountId = a.Id AND s.rn = 1
            LEFT JOIN SubName sn ON sn.SbscriptionId = s.SbscriptionId AND sn.rn = 1
            LEFT JOIN UserAgg ua ON ua.AccountId = a.Id
            LEFT JOIN PrimaryUser pu ON pu.AccountId = a.Id
            LEFT JOIN Registration_Account_Status ras ON ras.Id = a.RegistrationStatusId
            WHERE ISNULL(a.IsArchived, 0) = 0
            ORDER BY a.Name;
            """;

        var syncSql = """
            WITH LastSync AS (
                SELECT
                    AccountId,
                    SyncStatus,
                    SyncEndDatetime,
                    ROW_NUMBER() OVER (
                        PARTITION BY AccountId
                        ORDER BY SyncEndDatetime DESC
                    ) AS rn
                FROM AccountingSystemCredential
            )
            SELECT AccountId, SyncStatus, SyncEndDatetime
            FROM LastSync
            WHERE rn = 1;
            """;

        Dictionary<Guid, SyncInfo> syncByAccount;
        try
        {
            syncByAccount = await GetSyncInfoAsync(syncSql, cancellationToken);
        }
        catch (SqlException)
        {
            syncByAccount = new Dictionary<Guid, SyncInfo>();
        }
        var usersByAccount = await GetUsersByAccountAsync(cancellationToken);

        await using (var connection = new SqlConnection(GetConnectionString("capassa-registration-db")))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(customerSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var accountId = reader.GetGuid(0);
                var syncInfo = syncByAccount.GetValueOrDefault(accountId);
                usersByAccount.TryGetValue(accountId, out var users);

                customers.Add(new CustomerOverview(
                    AccountId: accountId,
                    CustomerName: reader.GetString(1),
                    OrganizationNumber: GetNullable<long>(reader, 2),
                    SubscriptionName: GetNullableString(reader, 3),
                    UsersCount: reader.GetInt32(4),
                    LastLoginUtc: GetNullable<DateTime>(reader, 5),
                    PrimaryUserEmail: GetNullableString(reader, 6),
                    PrimaryUserName: GetNullableString(reader, 7),
                    IsDeleted: reader.GetBoolean(8),
                    IsDisabled: reader.GetBoolean(9),
                    IsActive: GetNullable<bool>(reader, 10),
                    RegistrationStatusId: GetNullable<int>(reader, 11),
                    RegistrationStatus: GetNullableString(reader, 12),
                    LastSyncStatus: syncInfo?.SyncStatus,
                    LastSyncEndUtc: syncInfo?.SyncEndDatetime,
                    Users: users ?? new List<CustomerUser>()));
            }
        }

        return customers;
    }

    public async Task<OrganizationLookupResult?> GetOrganizationLookupAsync(long organizationNumber, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                a.Id,
                a.Name,
                a.OrganizationNumber,
                a.IsArchived,
                a.IsActive
            FROM Registration_Account a
            WHERE a.OrganizationNumber = @OrganizationNumber;
            """;

        await using var connection = new SqlConnection(GetConnectionString("capassa-registration-db"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@OrganizationNumber", organizationNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OrganizationLookupResult(
            AccountId: reader.GetGuid(0),
            CustomerName: GetNullableString(reader, 1),
            OrganizationNumber: reader.GetInt64(2),
            IsArchived: GetNullable<bool>(reader, 3),
            IsActive: GetNullable<bool>(reader, 4));
    }

    public async Task<IReadOnlyList<DeletedCustomerFlagSummary>> GetDeletedCustomerFlagSummariesAsync(
        string namePrefix,
        CancellationToken cancellationToken)
    {
        var results = new List<DeletedCustomerFlagSummary>();

        const string sql = """
            SELECT
                a.IsArchived,
                a.IsActive,
                a.RegistrationStatusId,
                ras.Status AS RegistrationStatus,
                COUNT(*) AS Count
            FROM Registration_Account a
            LEFT JOIN Registration_Account_Status ras ON ras.Id = a.RegistrationStatusId
            WHERE a.Name LIKE @NamePrefix + '%'
            GROUP BY a.IsArchived, a.IsActive, a.RegistrationStatusId, ras.Status
            ORDER BY Count DESC;
            """;

        await using var connection = new SqlConnection(GetConnectionString("capassa-registration-db"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@NamePrefix", namePrefix);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DeletedCustomerFlagSummary(
                IsArchived: GetNullable<bool>(reader, 0),
                IsActive: GetNullable<bool>(reader, 1),
                RegistrationStatusId: GetNullable<int>(reader, 2),
                RegistrationStatus: GetNullableString(reader, 3),
                Count: reader.GetInt32(4)));
        }

        return results;
    }

    private async Task<Dictionary<Guid, List<CustomerUser>>> GetUsersByAccountAsync(CancellationToken cancellationToken)
    {
        var groupedUsers = new Dictionary<Guid, Dictionary<string, CustomerUserBuilder>>();

        var sql = """
            WITH AccountUsers AS (
                SELECT
                    CAST('Registration' AS nvarchar(32)) AS RoleSource,
                    AccountId,
                    UserId,
                    CAST(RoleId AS nvarchar(64)) AS RoleId,
                    Is_Default_Account,
                    RegisteredAs,
                    CAST(NULL AS nvarchar(256)) AS RoleEmail
                FROM Registration_Account_User_Role
                UNION ALL
                SELECT
                    CAST('Capassa' AS nvarchar(32)) AS RoleSource,
                    AccountId,
                    UserId,
                    CAST(RoleId AS nvarchar(64)) AS RoleId,
                    Is_Default_Account,
                    RegisteredAs,
                    Email AS RoleEmail
                FROM Capassa_Account_User_Role
            ),
            UserLastLogin AS (
                SELECT
                    RegistrationUserId,
                    MAX(Date) AS LastLoginUtc
                FROM User_Login_History
                GROUP BY RegistrationUserId
            )
            SELECT
                au.AccountId,
                au.UserId,
                COALESCE(u.Email, au.RoleEmail) AS Email,
                NULLIF(LTRIM(RTRIM(CONCAT(u.First_Name, ' ', u.Last_Name))), '') AS FullName,
                ull.LastLoginUtc,
                au.RoleSource,
                au.RoleId,
                au.Is_Default_Account,
                au.RegisteredAs
            FROM AccountUsers au
            INNER JOIN Registration_Account a ON a.Id = au.AccountId
            LEFT JOIN Registration_User u ON u.Id = au.UserId
            LEFT JOIN UserLastLogin ull ON ull.RegistrationUserId = au.UserId
            WHERE ISNULL(a.IsArchived, 0) = 0
              AND COALESCE(CONVERT(nvarchar(36), au.UserId), LOWER(COALESCE(u.Email, au.RoleEmail))) IS NOT NULL
              AND (u.Id IS NULL OR ISNULL(u.IsArchived, 0) = 0)
            ORDER BY au.AccountId, Email;
            """;

        await using var connection = new SqlConnection(GetConnectionString("capassa-registration-db"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var accountId = reader.GetGuid(0);
            var userId = GetNullable<Guid>(reader, 1);
            var email = GetNullableString(reader, 2);
            var fullName = GetNullableString(reader, 3);
            var lastLoginUtc = GetNullable<DateTime>(reader, 4);
            var roleSource = GetNullableString(reader, 5);
            var roleId = GetNullableString(reader, 6);
            var isDefaultAccount = GetNullable<bool>(reader, 7);
            var registeredAs = GetNullable<bool>(reader, 8);

            var userKey = userId?.ToString() ?? email?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(userKey))
            {
                continue;
            }

            if (!groupedUsers.TryGetValue(accountId, out var accountUsers))
            {
                accountUsers = new Dictionary<string, CustomerUserBuilder>(StringComparer.OrdinalIgnoreCase);
                groupedUsers[accountId] = accountUsers;
            }

            if (!accountUsers.TryGetValue(userKey, out var user))
            {
                user = new CustomerUserBuilder
                {
                    UserId = userId,
                    Email = email,
                    FullName = fullName,
                    LastLoginUtc = lastLoginUtc
                };
                accountUsers[userKey] = user;
            }

            user.UserId ??= userId;
            user.Email ??= email;
            user.FullName ??= fullName;
            if (lastLoginUtc is not null && (user.LastLoginUtc is null || lastLoginUtc > user.LastLoginUtc))
            {
                user.LastLoginUtc = lastLoginUtc;
            }

            var roleLabel = BuildRoleLabel(roleSource, roleId, isDefaultAccount, registeredAs);
            if (!string.IsNullOrWhiteSpace(roleLabel))
            {
                user.Roles.Add(roleLabel);
            }
        }

        var results = new Dictionary<Guid, List<CustomerUser>>();
        foreach (var accountEntry in groupedUsers)
        {
            var users = accountEntry.Value.Values
                .Select(user => new CustomerUser(
                    UserId: user.UserId,
                    Email: user.Email,
                    FullName: user.FullName,
                    LastLoginUtc: user.LastLoginUtc,
                    Roles: user.Roles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase).ToList()))
                .OrderBy(user => user.Email ?? user.FullName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            results[accountEntry.Key] = users;
        }

        return results;
    }

    private async Task<Dictionary<Guid, SyncInfo>> GetSyncInfoAsync(string sql, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, SyncInfo>();

        await using var connection = new SqlConnection(GetConnectionString("capassa-financedata-db"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var accountId = reader.GetGuid(0);
            var syncStatus = GetNullable<int>(reader, 1);
            var syncEndDatetime = GetNullable<DateTime>(reader, 2);

            results[accountId] = new SyncInfo(accountId, syncStatus, syncEndDatetime);
        }

        return results;
    }

    private string GetConnectionString(string databaseName)
    {
        var baseConnectionString = _configuration.GetConnectionString("StatsDb");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:StatsDb is not configured.");
        }

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName
        };

        return builder.ConnectionString;
    }

    private static T? GetNullable<T>(SqlDataReader reader, int ordinal) where T : struct
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static string? GetNullableString(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string BuildRoleLabel(string? roleSource, string? roleId, bool? isDefaultAccount, bool? registeredAs)
    {
        var source = string.IsNullOrWhiteSpace(roleSource) ? "Unknown" : roleSource;
        var normalizedRoleId = roleId?.Trim();
        var normalizedRoleKey = normalizedRoleId?.ToUpperInvariant();

        var friendlyRole = normalizedRoleKey switch
        {
            "1" => "Eier",
            "2" => "Medlem",
            "3" => "Investor",
            "302C99E3-E66C-4BCA-B2C9-47D70D8D55C8" => "Eier",
            "BA2C54D5-AE5E-46B5-89E0-B2DECA879879" => "Medlem",
            "7DCE6E4E-C17E-43EF-9E73-3A780D52C927" => "Regnskapsfører",
            "8EAF290D-1ED8-4199-857A-18EAC7DC4711" => "Styremedlem",
            _ => null
        };

        var rolePart = friendlyRole is null
            ? $"RoleId {normalizedRoleId ?? "ukjent"}"
            : friendlyRole;

        var flags = new List<string>();
        if (isDefaultAccount == true)
        {
            flags.Add("Default");
        }

        if (registeredAs == true)
        {
            flags.Add("RegisteredAs");
        }

        var suffix = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : string.Empty;
        return $"{source}: {rolePart}{suffix}";
    }

    private sealed class CustomerUserBuilder
    {
        public Guid? UserId { get; set; }
        public string? Email { get; set; }
        public string? FullName { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public HashSet<string> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SyncInfo(Guid AccountId, int? SyncStatus, DateTime? SyncEndDatetime);
}
