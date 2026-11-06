using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace VRCX
{
    public class SQLite
    {
        public static SQLite Instance;
        private readonly ReaderWriterLockSlim m_ConnectionLock;
        private SQLiteConnection m_Connection;

        static SQLite()
        {
            Instance = new SQLite();
        }

        public SQLite()
        {
            m_ConnectionLock = new ReaderWriterLockSlim();
        }

        public void Init()
        {
#if LINUX
            Instance = this;
#endif
            var dataSource = Program.ConfigLocation;
            var jsonDataSource = VRCXStorage.Instance.Get("VRCX_DatabaseLocation");
            if (!string.IsNullOrEmpty(jsonDataSource))
                dataSource = jsonDataSource;

            m_Connection = new SQLiteConnection($"Data Source=\"{dataSource}\";Version=3;PRAGMA locking_mode=NORMAL;PRAGMA busy_timeout=5000;PRAGMA journal_mode=WAL;PRAGMA optimize=0x10002;", true);

            m_Connection.Open();
        }

        public void Exit()
        {
            m_ConnectionLock.EnterWriteLock();
            try
            {
                m_Connection.Close();
                m_Connection.Dispose();
            }
            finally
            {
                m_ConnectionLock.ExitWriteLock();
            }
        }

        // for Electron
        public string ExecuteJson(string sql, IDictionary<string, object>? args = null)
        {
            var result = Execute(sql, args);
            return JsonSerializer.Serialize(result);
        }

        public object[][] Execute(string sql, IDictionary<string, object>? args = null)
        {
            m_ConnectionLock.EnterReadLock();
            try
            {
                using var command = new SQLiteCommand(sql, m_Connection);
                if (args != null)
                {
                    foreach (var arg in args)
                    {
                        command.Parameters.Add(new SQLiteParameter(arg.Key, arg.Value));
                    }
                }

                using var reader = command.ExecuteReader();
                var result = new List<object[]>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        values[i] = reader.GetValue(i);
                    }
                    result.Add(values);
                }
                return result.ToArray();
            }
            finally
            {
                m_ConnectionLock.ExitReadLock();
            }
        }

        public int ExecuteNonQuery(string sql, IDictionary<string, object>? args = null)
        {
            var result = -1;
            m_ConnectionLock.EnterWriteLock();
            try
            {
                using var command = new SQLiteCommand(sql, m_Connection);
                if (args != null)
                {
                    foreach (var arg in args)
                    {
                        command.Parameters.Add(new SQLiteParameter(arg.Key, arg.Value));
                    }
                }
                result = command.ExecuteNonQuery();
            }
            finally
            {
                m_ConnectionLock.ExitWriteLock();
            }

            return result;
        }

        public long ExecuteInsert(string sql, IDictionary<string, object>? args = null)
        {
            m_ConnectionLock.EnterWriteLock();
            try
            {
                using var command = new SQLiteCommand(sql, m_Connection);
                if (args != null)
                {
                    foreach (var arg in args)
                    {
                        command.Parameters.Add(new SQLiteParameter(arg.Key, arg.Value));
                    }
                }

                return command.ExecuteNonQuery() > 0 ? m_Connection.LastInsertRowId : 0;
            }
            finally
            {
                m_ConnectionLock.ExitWriteLock();
            }
        }

        // The hidden server renderer publishes a read-only web projection in one
        // transaction. Keeping the batch in this existing SQLite service means
        // the renderer remains the only database writer and no SQL bridge is
        // exposed to the browser-facing server.
        public int ExecuteTransactionJson(string operationsJson)
        {
            var operations = JsonSerializer.Deserialize<List<SQLiteTransactionOperation>>(
                operationsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new List<SQLiteTransactionOperation>();
            if (operations.Count > 50000)
                throw new ArgumentOutOfRangeException(nameof(operationsJson), "Too many SQLite transaction operations");

            m_ConnectionLock.EnterWriteLock();
            try
            {
                using var transaction = m_Connection.BeginTransaction();
                var affectedRows = 0;
                foreach (var operation in operations)
                {
                    if (string.IsNullOrWhiteSpace(operation.Sql))
                        throw new ArgumentException("SQLite transaction operation SQL must not be empty", nameof(operationsJson));

                    using var command = new SQLiteCommand(operation.Sql, m_Connection, transaction);
                    if (operation.Args != null)
                    {
                        foreach (var arg in operation.Args)
                            command.Parameters.Add(new SQLiteParameter(arg.Key, ConvertJsonValue(arg.Value)));
                    }
                    affectedRows += command.ExecuteNonQuery();
                }
                transaction.Commit();
                return affectedRows;
            }
            finally
            {
                m_ConnectionLock.ExitWriteLock();
            }
        }

        private static object ConvertJsonValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.Null => DBNull.Value,
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number => value.GetDouble(),
                _ => value.GetRawText()
            };
        }

        private sealed class SQLiteTransactionOperation
        {
            public string Sql { get; set; } = string.Empty;
            public Dictionary<string, JsonElement>? Args { get; set; }
        }
    }
}
