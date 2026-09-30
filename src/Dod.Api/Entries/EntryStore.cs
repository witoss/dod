using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Entries;

public sealed class EntryStore(IConfiguration configuration)
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
        if (version > 3)
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
        transaction.Commit();
    }

    public async Task<int?> GetCalorieReferenceAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Calories FROM CalorieReference WHERE Id = 1";
        var value = await command.ExecuteScalarAsync();
        return value is null ? null : Convert.ToInt32(value);
    }

    public async Task SaveCalorieReferenceAsync(int calories)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CalorieReference (Id, Calories) VALUES (1, $calories)
            ON CONFLICT(Id) DO UPDATE SET Calories = excluded.Calories
            """;
        command.Parameters.AddWithValue("$calories", calories);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<DailyEntry>> ListAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Date, WeightKg, CaloriesBurned, CaloriesEaten FROM Entries ORDER BY Date DESC";
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
        command.CommandText = "UPDATE Entries SET CaloriesEaten = NULL WHERE Date = $date";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private async Task SaveAsync(DateOnly date, string column, object value)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        // Column is chosen only by the measurement methods above; all input values are parameters.
        command.CommandText = $"INSERT INTO Entries (Date, {column}) VALUES ($date, $value) ON CONFLICT(Date) DO UPDATE SET {column} = excluded.{column}";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(connectionString);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
