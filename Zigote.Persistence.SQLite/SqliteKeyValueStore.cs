using Microsoft.Data.Sqlite;

namespace Zigote.Persistence.SQLite;

/// <summary>
///     SQLite-backed <see cref="IKeyValueStore" /> built on <c>Microsoft.Data.Sqlite</c> (bundled
///     native <c>e_sqlite3</c> — no hand-rolled P/Invoke). One
///     <c>(key TEXT PRIMARY KEY, value TEXT NOT NULL)</c> table per store; writes are upserts,
///     durable immediately, so <see cref="Flush" /> is a no-op. <c>journal_mode=WAL</c> keeps
///     concurrent readers cheap. One connection per store, pooling disabled, guarded by a lock, so
///     disposal releases the database file deterministically.
///     <para>
///         Multiple stores may share one database file with different <paramref name="tableName" />s.
///         The table name must match <c>[A-Za-z_][A-Za-z0-9_]*</c> — identifiers cannot be
///         parameterized, so the name is validated instead of interpolating caller input into SQL.
///     </para>
/// </summary>
public sealed class SqliteKeyValueStore : IKeyValueStore
{
    private readonly SqliteConnection _connection;
    private readonly string _table;

    public SqliteKeyValueStore(string path, string tableName = "preferences")
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ValidateTableName(tableName);
        _table = tableName;

        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = path,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute(
            $"CREATE TABLE IF NOT EXISTS \"{_table}\" (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
        );
    }

    public bool TryGet(string key, out string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT value FROM \"{_table}\" WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var result = command.ExecuteScalar();
            if (result is string s)
            {
                value = s;
                return true;
            }

            value = null!;
            return false;
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO \"{_table}\" (key, value) VALUES ($key, $value) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"{_table}\" WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT 1 FROM \"{_table}\" WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() is not null;
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT key FROM \"{_table}\" ORDER BY key;";
            using var reader = command.ExecuteReader();
            var keys = new List<string>();
            while (reader.Read()) keys.Add(reader.GetString(0));
            return keys;
        }
    }

    public void Clear()
    {
        lock (_connection)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"{_table}\";";
            command.ExecuteNonQuery();
        }
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
        lock (_connection)
        {
            _connection.Dispose();
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ValidateTableName(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        var first = tableName[0];
        var valid = char.IsAsciiLetter(first) || first == '_';
        for (var i = 1; valid && i < tableName.Length; i++)
        {
            var c = tableName[i];
            valid = char.IsAsciiLetterOrDigit(c) || c == '_';
        }

        if (!valid)
            throw new ArgumentException(
                $"Table name '{tableName}' must match [A-Za-z_][A-Za-z0-9_]*.",
                nameof(tableName)
            );
    }
}