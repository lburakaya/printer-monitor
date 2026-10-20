using System.ComponentModel.DataAnnotations;

namespace PrinterMonitor;

public sealed class MonitorOptions
{
    public bool DemoMode { get; set; } = true;
    public int PollSeconds { get; set; } = 300;
    public int TimeoutMs { get; set; } = 2500;
    public int Retries { get; set; } = 1;
    public int MaxConcurrency { get; set; } = 4;
    public int LowThreshold { get; set; } = 20;
    public int HistoryDays { get; set; } = 30;
    public string DataDirectory { get; set; } = "data";
    public string[] AllowedNetworks { get; set; } = [];
    public string? AdminPassword { get; set; }

    public void Validate()
    {
        if (PollSeconds is < 30 or > 86400 || TimeoutMs is < 100 or > 10000 || Retries is < 0 or > 2
            || MaxConcurrency is < 1 or > 16 || LowThreshold is < 1 or > 99 || HistoryDays is < 1 or > 365)
            throw new InvalidOperationException("Monitor ayarları geçersiz. README içindeki sınırları kontrol edin.");
        if (!DemoMode && (string.IsNullOrWhiteSpace(AdminPassword) || AdminPassword.Length < 12))
            throw new InvalidOperationException("Gerçek cihaz modu için Monitor__AdminPassword en az 12 karakter olmalı.");
        if (!DemoMode && AllowedNetworks.Length == 0)
            throw new InvalidOperationException("Gerçek cihaz modu için Monitor:AllowedNetworks yapılandırılmalı.");
    }
}

public sealed record Credentials(string Community = "", string Username = "", string AuthPassword = "", string PrivacyPassword = "");

public sealed class PrinterInput
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; set; } = "";
    [Required, StringLength(253)] public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 161;
    [StringLength(100)] public string Location { get; set; } = "";
    [RegularExpression("^(v1|v2c|v3)$")] public string Version { get; set; } = "v2c";
    [RegularExpression("^(SHA1|SHA256)$")] public string AuthProtocol { get; set; } = "SHA256";
    [StringLength(255)] public string? Community { get; set; }
    [StringLength(64)] public string? Username { get; set; }
    [StringLength(255)] public string? AuthPassword { get; set; }
    [StringLength(255)] public string? PrivacyPassword { get; set; }
    [RegularExpression("^(healthy|low|unreachable|unknown)$")] public string DemoScenario { get; set; } = "healthy";

    public void Validate()
    {
        Name = (Name ?? "").Trim(); Host = (Host ?? "").Trim(); Location = (Location ?? "").Trim();
        Validator.ValidateObject(this, new ValidationContext(this), true);
        if (Uri.CheckHostName(Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            throw new ValidationException("Geçerli bir IPv4 adresi veya sunucu adı girin; URL kullanmayın.");
    }

    public Credentials GetCredentials(Credentials? old, bool demo)
    {
        var c = new Credentials(Community ?? old?.Community ?? "", Username ?? old?.Username ?? "",
            AuthPassword ?? old?.AuthPassword ?? "", PrivacyPassword ?? old?.PrivacyPassword ?? "");
        if (!demo && Version != "v3" && string.IsNullOrWhiteSpace(c.Community))
            throw new ValidationException("Read-only community gerekli.");
        if (!demo && Version == "v3" && (string.IsNullOrWhiteSpace(c.Username) || c.AuthPassword.Length < 8 || c.PrivacyPassword.Length < 8))
            throw new ValidationException("SNMPv3 kullanıcı adı ve en az 8 karakterli kimlik doğrulama/şifreleme parolaları gerekli.");
        return Version == "v3" ? c with { Community = "" } : new Credentials(c.Community);
    }
}

public sealed record PrinterConfig(Guid Id, string Name, string Host, int Port, string Location,
    string Version, string AuthProtocol, string DemoScenario, string Revision);
public sealed record Supply(string Index, string Name, string Color, int? Percent, string State,
    int? Level, int? Capacity, int? Unit, bool IsReceptacle);
public sealed record PrinterReading(string Status, string Message, string? Model, string? Serial,
    List<Supply> Supplies, DateTimeOffset AttemptedAt, DateTimeOffset? LastSuccessAt);
public sealed record PrinterRow(PrinterConfig Config, string ProtectedCredentials, PrinterReading? Reading);
public sealed record PrinterView(Guid Id, string Name, string Host, int Port, string Location, string Version,
    string AuthProtocol, string DemoScenario, bool HasCredentials, PrinterReading? Reading)
{
    public static PrinterView From(PrinterRow row) => new(row.Config.Id, row.Config.Name, row.Config.Host,
        row.Config.Port, row.Config.Location, row.Config.Version, row.Config.AuthProtocol,
        row.Config.DemoScenario, row.ProtectedCredentials.Length > 0, row.Reading);
}

public interface IPrinterAdapter
{
    Task<PrinterReading> ReadAsync(PrinterConfig config, Credentials credentials, CancellationToken cancellationToken);
}
