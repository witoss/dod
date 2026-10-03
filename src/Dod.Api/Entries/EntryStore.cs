using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Entries;

public sealed class EntryStore(IConfiguration configuration, IHttpContextAccessor context, TimeProvider clock)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = configuration["Storage:Path"] ?? "data/dod.db",
        ForeignKeys = true
    }.ToString();

    public async Task InitializeAsync()
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        await command.ExecuteNonQueryAsync();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (version > 6)
            throw new InvalidOperationException("The database was created by a newer version of DOD.");

        // Version 1 originally had no user_version. Adopt it without replacing any entries.
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Entries (
                Date TEXT PRIMARY KEY,
                WeightKg REAL NULL CHECK (WeightKg > 0 AND WeightKg <= 1000),
                CaloriesBurned INTEGER NULL CHECK (CaloriesBurned >= 0 AND CaloriesBurned <= 100000)
            );
            """;
        await command.ExecuteNonQueryAsync();
        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS CalorieReference (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Calories INTEGER NOT NULL CHECK (Calories > 0 AND Calories <= 100000)
                );
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync();
        }
        if (version < 3)
        {
            command.CommandText = """
                ALTER TABLE Entries ADD COLUMN CaloriesEaten INTEGER NULL
                    CHECK (CaloriesEaten >= 0 AND CaloriesEaten <= 100000);
                PRAGMA user_version=3;
                """;
            await command.ExecuteNonQueryAsync();
        }
        if (version < 4)
        {
            command.CommandText = """
                CREATE TABLE Users (
                    Id TEXT PRIMARY KEY, Nickname TEXT NOT NULL, NormalizedNickname TEXT NOT NULL UNIQUE,
                    PasswordHash TEXT NULL, Avatar BLOB NULL, AvatarType TEXT NULL
                );
                INSERT INTO Users (Id, Nickname, NormalizedNickname) VALUES ('legacy', 'Reserved owner', '!OWNER');
                ALTER TABLE Entries RENAME TO LegacyEntries;
                CREATE TABLE Entries (
                    UserId TEXT NOT NULL REFERENCES Users(Id), Date TEXT NOT NULL,
                    WeightKg REAL NULL CHECK (WeightKg > 0 AND WeightKg <= 1000),
                    CaloriesBurned INTEGER NULL CHECK (CaloriesBurned BETWEEN 0 AND 100000),
                    CaloriesEaten INTEGER NULL CHECK (CaloriesEaten BETWEEN 0 AND 100000),
                    PRIMARY KEY(UserId, Date)
                );
                INSERT INTO Entries SELECT 'legacy', Date, WeightKg, CaloriesBurned, CaloriesEaten FROM LegacyEntries;
                DROP TABLE LegacyEntries;
                ALTER TABLE CalorieReference RENAME TO LegacyReference;
                CREATE TABLE CalorieReference (UserId TEXT PRIMARY KEY REFERENCES Users(Id), Calories INTEGER NOT NULL CHECK (Calories BETWEEN 1 AND 100000));
                INSERT INTO CalorieReference SELECT 'legacy', Calories FROM LegacyReference;
                DROP TABLE LegacyReference;
                CREATE TABLE Friendships (
                    Sender TEXT NOT NULL REFERENCES Users(Id), Recipient TEXT NOT NULL REFERENCES Users(Id),
                    Status TEXT NOT NULL CHECK (Status IN ('pending','accepted')), CHECK(Sender <> Recipient),
                    PRIMARY KEY(Sender, Recipient)
                );
                CREATE UNIQUE INDEX FriendshipPair ON Friendships(min(Sender, Recipient), max(Sender, Recipient));
                CREATE TABLE Challenges (
                    Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL REFERENCES Users(Id), Name TEXT NOT NULL,
                    StartDate TEXT NOT NULL, EndDate TEXT NOT NULL
                );
                CREATE TABLE Participants (
                    ChallengeId TEXT NOT NULL REFERENCES Challenges(Id), UserId TEXT NOT NULL REFERENCES Users(Id),
                    InvitedBy TEXT NOT NULL REFERENCES Users(Id), Status TEXT NOT NULL CHECK(Status IN ('pending','accepted')),
                    PRIMARY KEY(ChallengeId, UserId)
                );
                PRAGMA user_version=4;
                """;
            await command.ExecuteNonQueryAsync();
        }
        if (version < 5)
        {
            command.Parameters.AddWithValue("$today", Dod.Api.Notifications.WeeklyRules.Day(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)));
            command.CommandText = """
                ALTER TABLE Users ADD COLUMN IsAdmin INTEGER NOT NULL DEFAULT 0 CHECK (IsAdmin IN (0,1));
                UPDATE Users SET IsAdmin=1 WHERE Id='legacy';
                CREATE TABLE Activities (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Kind TEXT NOT NULL CHECK (Kind IN ('manual','weight')),
                    Points INTEGER NOT NULL CHECK (Points BETWEEN 1 AND 1000),
                    CutoffTime TEXT NULL,
                    IsActive INTEGER NOT NULL CHECK (IsActive IN (0,1)),
                    AvailableFrom TEXT NOT NULL
                );
                CREATE UNIQUE INDEX SingleWeightActivity ON Activities(Kind) WHERE Kind='weight';
                INSERT INTO Activities VALUES
                    ('weight','Weigh yourself','Save your weight in your journal. Points are awarded automatically once per date.','weight',10,NULL,1,$today),
                    ('no-sweets','No sweets','Did you avoid sweets for this whole day?','manual',10,NULL,1,$today),
                    ('no-late-food','No food after cutoff','Did you avoid eating after the cutoff time for this day?','manual',10,'20:00',1,$today);
                CREATE TABLE ActivityReports (
                    UserId TEXT NOT NULL REFERENCES Users(Id),
                    ActivityId TEXT NOT NULL REFERENCES Activities(Id),
                    Date TEXT NOT NULL,
                    Completed INTEGER NOT NULL CHECK (Completed IN (0,1)),
                    Points INTEGER NOT NULL CHECK (Points BETWEEN 0 AND 1000),
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    CutoffTime TEXT NULL,
                    PRIMARY KEY(UserId,ActivityId,Date)
                );
                -- Existing weights remain recorded but earn no retrospective XP.
                INSERT INTO ActivityReports
                    SELECT UserId,'weight',Date,1,0,'Weigh yourself',
                        'Recorded before the XP system was introduced.',NULL
                    FROM Entries WHERE WeightKg IS NOT NULL;
                PRAGMA user_version=5;
                """;
            await command.ExecuteNonQueryAsync();
        }
        if (version < 6)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            command.Parameters.AddWithValue("$eligible", Dod.Api.Notifications.WeeklyRules.Day(Dod.Api.Notifications.WeeklyRules.NextMonday(today)));
            command.Parameters.AddWithValue("$monday", Dod.Api.Notifications.WeeklyRules.Day(Dod.Api.Notifications.WeeklyRules.Monday(today)));
            command.CommandText = """
                ALTER TABLE Users ADD COLUMN WeeklyEligibleFrom TEXT NOT NULL DEFAULT '';
                UPDATE Users SET WeeklyEligibleFrom=$eligible;
                CREATE TABLE WeeklyActivitySchedule (
                    ActivityId TEXT NOT NULL REFERENCES Activities(Id), EffectiveFrom TEXT NOT NULL,
                    Name TEXT NOT NULL, IsActive INTEGER NOT NULL CHECK(IsActive IN (0,1)),
                    PRIMARY KEY(ActivityId,EffectiveFrom)
                );
                INSERT INTO WeeklyActivitySchedule SELECT Id,$monday,Name,IsActive FROM Activities;
                CREATE TABLE WeeklyAwards (
                    UserId TEXT NOT NULL REFERENCES Users(Id), WeekStart TEXT NOT NULL,
                    Points INTEGER NOT NULL CHECK(Points IN (0,50)), PRIMARY KEY(UserId,WeekStart)
                );
                CREATE TABLE EmailPreferences (
                    UserId TEXT PRIMARY KEY REFERENCES Users(Id), Email TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 0, Verified INTEGER NOT NULL DEFAULT 0,
                    Revision TEXT NOT NULL, TokenHash TEXT NULL, TokenExpires INTEGER NULL,
                    LastVerification INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE EmailOutbox (
                    Id TEXT PRIMARY KEY, UserId TEXT NOT NULL REFERENCES Users(Id), Revision TEXT NOT NULL,
                    Kind TEXT NOT NULL, WeekStart TEXT NULL, Payload TEXT NOT NULL,
                    State TEXT NOT NULL DEFAULT 'pending', Attempts INTEGER NOT NULL DEFAULT 0,
                    NextAttempt INTEGER NOT NULL, LeaseUntil INTEGER NULL, SentAt INTEGER NULL
                );
                CREATE UNIQUE INDEX OneWeeklyEmail ON EmailOutbox(UserId,WeekStart) WHERE Kind='weekly';
                CREATE INDEX PendingEmails ON EmailOutbox(State,NextAttempt);
                PRAGMA user_version=6;
                """;
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task<int?> GetCalorieReferenceAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$user", UserId);
        command.CommandText = "SELECT Calories FROM CalorieReference WHERE UserId = $user";
        var value = await command.ExecuteScalarAsync();
        return value is null ? null : Convert.ToInt32(value);
    }

    public async Task SaveCalorieReferenceAsync(int calories)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$user", UserId);
        command.CommandText = """
            INSERT INTO CalorieReference (UserId, Calories) VALUES ($user, $calories)
            ON CONFLICT(UserId) DO UPDATE SET Calories = excluded.Calories
            """;
        command.Parameters.AddWithValue("$calories", calories);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<DailyEntry>> ListAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$user", UserId);
        command.CommandText = "SELECT Date, WeightKg, CaloriesBurned, CaloriesEaten FROM Entries WHERE UserId = $user ORDER BY Date DESC";
        using var reader = await command.ExecuteReaderAsync();
        var entries = new List<DailyEntry>();
        while (await reader.ReadAsync())
            entries.Add(new DailyEntry(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : reader.GetDecimal(1), reader.IsDBNull(2) ? null : reader.GetInt32(2))
            { CaloriesEaten = reader.IsDBNull(3) ? null : reader.GetInt32(3) });
        return entries;
    }

    public Task SaveWeightAsync(DateOnly date, decimal weight) => SaveAsync(date, "WeightKg", weight);
    public Task SaveCaloriesAsync(DateOnly date, int calories) => SaveAsync(date, "CaloriesBurned", calories);

    public async Task SaveEatenAsync(DateOnly date, int? calories)
    {
        if (calories.HasValue)
        {
            await SaveAsync(date, "CaloriesEaten", calories.Value);
            return;
        }
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$user", UserId);
        command.CommandText = "UPDATE Entries SET CaloriesEaten = NULL WHERE UserId = $user AND Date = $date";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private async Task SaveAsync(DateOnly date, string column, object value)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$user", UserId);
        // Column is chosen only by the measurement methods above; all input values are parameters.
        command.CommandText = $"INSERT INTO Entries (UserId, Date, {column}) VALUES ($user, $date, $value) ON CONFLICT(UserId, Date) DO UPDATE SET {column} = excluded.{column}";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    public string UserId => context.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException();

    internal async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(connectionString);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
