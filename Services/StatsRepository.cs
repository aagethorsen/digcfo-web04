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
                (SELECT COUNT(*) FROM Registration_Account WHERE IsArchived = 0) AS TotalCustomers,
                (SELECT COUNT(*) FROM Registration_Account WHERE IsArchived = 0 AND IsActive = 1) AS ActiveCustomers,
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
            WITH ActiveSub AS (
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
                    aur.AccountId,
                    COUNT(DISTINCT aur.UserId) AS UsersCount,
                    MAX(ulh.Date) AS LastLoginUtc
                FROM Registration_Account_User_Role aur
                LEFT JOIN User_Login_History ulh ON ulh.RegistrationUserId = aur.UserId
                GROUP BY aur.AccountId
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
                pu.PrimaryUserName
            FROM Registration_Account a
            LEFT JOIN ActiveSub s ON s.AccountId = a.Id AND s.rn = 1
            LEFT JOIN SubName sn ON sn.SbscriptionId = s.SbscriptionId AND sn.rn = 1
            LEFT JOIN UserAgg ua ON ua.AccountId = a.Id
            LEFT JOIN PrimaryUser pu ON pu.AccountId = a.Id
            WHERE a.IsArchived = 0
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

        var syncByAccount = await GetSyncInfoAsync(syncSql, cancellationToken);
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
                    LastSyncStatus: syncInfo?.SyncStatus,
                    LastSyncEndUtc: syncInfo?.SyncEndDatetime,
                    Users: users ?? new List<CustomerUser>()));
            }
        }

        return customers;
    }

    private async Task<Dictionary<Guid, List<CustomerUser>>> GetUsersByAccountAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, List<CustomerUser>>();

        var sql = """
            WITH UserLastLogin AS (
                SELECT
                    RegistrationUserId,
                    MAX(Date) AS LastLoginUtc
                FROM User_Login_History
                GROUP BY RegistrationUserId
            )
            SELECT
                aur.AccountId,
                u.Id AS UserId,
                u.Email,
                CONCAT(u.First_Name, ' ', u.Last_Name) AS FullName,
                ull.LastLoginUtc
            FROM Registration_Account_User_Role aur
            INNER JOIN Registration_Account a ON a.Id = aur.AccountId AND a.IsArchived = 0
            INNER JOIN Registration_User u ON u.Id = aur.UserId AND u.IsArchived = 0
            LEFT JOIN UserLastLogin ull ON ull.RegistrationUserId = u.Id
            ORDER BY aur.AccountId, u.Email;
            """;

        await using var connection = new SqlConnection(GetConnectionString("capassa-registration-db"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var accountId = reader.GetGuid(0);
            var user = new CustomerUser(
                UserId: reader.GetGuid(1),
                Email: GetNullableString(reader, 2),
                FullName: GetNullableString(reader, 3),
                LastLoginUtc: GetNullable<DateTime>(reader, 4));

            if (!results.TryGetValue(accountId, out var list))
            {
                list = new List<CustomerUser>();
                results[accountId] = list;
            }

            list.Add(user);
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

    private sealed record SyncInfo(Guid AccountId, int? SyncStatus, DateTime? SyncEndDatetime);
}
