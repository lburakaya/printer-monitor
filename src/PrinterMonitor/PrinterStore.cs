using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace PrinterMonitor;

public sealed class PrinterStore
{
    private readonly string connectionString;
    private readonly IDataProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PrinterStore(MonitorOptions options, IDataProtectionProvider protection)
    {
        Directory.CreateDirectory(options.DataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DataDirectory, options.DemoMode ? "demo.db" : "printers.db"),
            DefaultTimeout = 10
        }.ToString();
        protector = protection.CreateProtector("PrinterMonitor.Credentials.v1");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS printers (
                id TEXT PRIMARY KEY, config TEXT NOT NULL, secrets TEXT NOT NULL,
                reading TEXT, revision TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT, printer_id TEXT NOT NULL,
                at TEXT NOT NULL, reading TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history ON history(printer_id, at);
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public Credentials Secrets(PrinterRow row) => JsonSerializer.Deserialize<Credentials>(protector.Unprotect(row.ProtectedCredentials), Json)!;

    public List<PrinterRow> All()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT config,secrets,reading FROM printers ORDER BY rowid";
        using var reader = cmd.ExecuteReader(); var rows = new List<PrinterRow>();
        while (reader.Read()) rows.Add(new(JsonSerializer.Deserialize<PrinterConfig>(reader.GetString(0), Json)!,
            reader.GetString(1), reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<PrinterReading>(reader.GetString(2), Json)));
        return rows;
    }
    public PrinterRow? Find(Guid id) => All().FirstOrDefault(x => x.Config.Id == id);

    public PrinterRow Save(PrinterInput input, Credentials credentials, Guid? id = null)
    {
        var config = new PrinterConfig(id ?? Guid.NewGuid(), input.Name, input.Host, input.Port, input.Location,
            input.Version, input.AuthProtocol, input.DemoScenario, Guid.NewGuid().ToString());
        var encrypted = protector.Protect(JsonSerializer.Serialize(credentials, Json));
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO printers(id,config,secrets,revision) VALUES($id,$config,$secrets,$revision)
            ON CONFLICT(id) DO UPDATE SET config=$config,secrets=$secrets,revision=$revision,reading=NULL;
            """;
        cmd.Parameters.AddWithValue("$id", config.Id.ToString());
        cmd.Parameters.AddWithValue("$config", JsonSerializer.Serialize(config, Json));
        cmd.Parameters.AddWithValue("$secrets", encrypted);
        cmd.Parameters.AddWithValue("$revision", config.Revision);
        cmd.ExecuteNonQuery();
        return new(config, encrypted, null);
    }

    public bool Record(PrinterConfig config, PrinterReading reading)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE printers SET reading=$reading WHERE id=$id AND revision=$revision";
        cmd.Parameters.AddWithValue("$id", config.Id.ToString());
        cmd.Parameters.AddWithValue("$revision", config.Revision);
        cmd.Parameters.AddWithValue("$reading", JsonSerializer.Serialize(reading, Json));
        if (cmd.ExecuteNonQuery() == 0) return false;
        cmd.CommandText = "INSERT INTO history(printer_id,at,reading) VALUES($id,$at,$reading)";
        cmd.Parameters.AddWithValue("$at", reading.AttemptedAt.ToString("O"));
        cmd.ExecuteNonQuery(); transaction.Commit(); return true;
    }

    public void Delete(Guid id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM history WHERE printer_id=$id; DELETE FROM printers WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public List<PrinterReading> History(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT reading FROM history WHERE printer_id=$id ORDER BY id DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = cmd.ExecuteReader(); var items = new List<PrinterReading>();
        while (reader.Read()) items.Add(JsonSerializer.Deserialize<PrinterReading>(reader.GetString(0), Json)!);
        return items;
    }
    public void Prune(int days)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O")); cmd.ExecuteNonQuery();
    }
}
