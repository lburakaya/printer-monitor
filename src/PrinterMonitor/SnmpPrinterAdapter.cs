using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace PrinterMonitor;

public sealed class SnmpPrinterAdapter(MonitorOptions options, NetworkPolicy policy) : IPrinterAdapter
{
    public const string SuppliesRoot = "1.3.6.1.2.1.43.11.1.1";
    public async Task<PrinterReading> ReadAsync(PrinterConfig config, Credentials credentials, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        string? model = null;
        bool reached = false;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromSeconds(45));
        var token = overall.Token;
        try
        {
            var endpoint = await policy.ResolveAsync(config.Host, config.Port, token);
            var session = new Session(config, credentials, endpoint, options);
            await session.InitializeAsync(token);
            var identity = await session.RequestAsync("1.3.6.1.2.1.1.1.0", false, token);
            reached = true;
            model = Useful(identity?.Data) ? Text(identity!.Data) : null;
            var supplies = ParseSupplies(await session.WalkAsync(SuppliesRoot, 512, token));
            string? serial = null;
            var message = supplies.Count == 0 ? "SNMP erişilebilir; standart sarf malzemesi tablosu bulunamadı." : "Standart Printer-MIB verileri okundu.";
            // Serial is optional. Its failure must not discard a successful supply reading.
            try { serial = (await session.WalkAsync("1.3.6.1.2.1.43.5.1.1.17", 16, token)).FirstOrDefault()?.Data.ToString(); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { message += " Seri numarası okunamadı."; }
            return new("online", message, model, serial, supplies, now, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ValidationException ex) { return Failure("blocked", ex.Message); }
        catch (SnmpRejectedException) { return Failure("error", "SNMP isteği reddedildi. Sürüm, kullanıcı, güvenlik ayarları ve okuma izinlerini kontrol edin."); }
        catch (Exception ex) when (ex is OperationCanceledException or System.TimeoutException or SocketException or Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            return Failure(reached ? "error" : "unreachable", reached
                ? "SNMP yanıt verdi fakat ölçüm tamamlanamadı. Önceki değerler korunuyor."
                : "SNMP yanıtı alınamadı. IP, UDP portu, ağ erişimi ve SNMP bilgilerini kontrol edin; neden kesin olarak belirlenemedi.");
        }
        catch (Exception) { return Failure("error", "SNMP yanıtı işlenemedi. Cihazın sürüm ve güvenlik ayarlarını kontrol edin."); }

        PrinterReading Failure(string state, string message) => new(state, message, model, null, [], now, null);
    }

    private static bool Useful(ISnmpData? data) => data is not null and not NoSuchInstance and not NoSuchObject and not EndOfMibView and not Null;
    private static string Text(ISnmpData data) { var text = data.ToString() ?? ""; return text.Length > 400 ? text[..400] : text; }

    public static List<Supply> ParseSupplies(IEnumerable<Variable> variables)
    {
        var rows = new Dictionary<string, Dictionary<int, ISnmpData>>();
        foreach (var variable in variables)
        {
            var oid = variable.Id.ToString().TrimStart('.');
            if (!oid.StartsWith(SuppliesRoot + ".", StringComparison.Ordinal) || !Useful(variable.Data)) continue;
            var suffix = oid[(SuppliesRoot.Length + 1)..].Split('.');
            if (suffix.Length < 3 || !int.TryParse(suffix[0], out var column)) continue;
            var index = string.Join('.', suffix.Skip(1));
            if (!rows.TryGetValue(index, out var row)) rows[index] = row = new();
            row[column] = variable.Data;
        }
        return rows.Select(pair =>
        {
            var row = pair.Value;
            int? Number(int column) => row.TryGetValue(column, out var data) && int.TryParse(data.ToString(), out var number) ? number : null;
            var name = row.TryGetValue(6, out var description) ? Text(description) : $"Sarf malzemesi {pair.Key}";
            var level = Number(9); var capacity = Number(8); var unit = Number(7);
            var percent = Percentage(level, capacity, unit);
            var state = percent.HasValue ? "measured" : level == -3 ? "someRemaining" : "unknown";
            var lower = name.ToLowerInvariant();
            var color = lower.Contains("cyan") ? "cyan" : lower.Contains("magenta") ? "magenta" : lower.Contains("yellow") || lower.Contains("sarı") ? "yellow" : lower.Contains("black") || lower.Contains("siyah") ? "black" : "other";
            return new Supply(pair.Key, name, color, percent, state, level, capacity, unit, Number(4) == 4);
        }).ToList();
    }

    public static int? Percentage(int? level, int? capacity, int? unit)
    {
        if (level is null or < 0) return null;
        if (unit == 19) return level <= 100 ? level : null; // Printer-MIB percent(19)
        if (capacity is null or <= 0 || level > capacity) return null;
        return (int)Math.Round(100d * level.Value / capacity.Value, MidpointRounding.AwayFromZero);
    }

    private sealed class Session(PrinterConfig config, Credentials credentials, IPEndPoint endpoint, MonitorOptions options)
    {
        private readonly VersionCode version = config.Version switch { "v1" => VersionCode.V1, "v3" => VersionCode.V3, _ => VersionCode.V2 };
        private ISnmpMessage? report;
        private IPrivacyProvider? privacy;

        public async Task InitializeAsync(CancellationToken token)
        {
            if (version != VersionCode.V3) return;
            // Explicit compatibility option for older printers; SHA256 is the default.
#pragma warning disable CS0618
            IAuthenticationProvider auth = config.AuthProtocol == "SHA1"
                ? new SHA1AuthenticationProvider(new OctetString(credentials.AuthPassword))
                : new SHA256AuthenticationProvider(new OctetString(credentials.AuthPassword));
#pragma warning restore CS0618
            privacy = new AESPrivacyProvider(new OctetString(credentials.PrivacyPassword), auth);
            report = await RetryAsync(ct => new Discovery(Messenger.NextMessageId, Messenger.NextRequestId, 65507)
                .GetResponseAsync(endpoint, ct), token);
        }

        public async Task<Variable?> RequestAsync(string oid, bool next, CancellationToken token)
        {
            for (var resync = 0; resync < 2; resync++)
            {
                IList<Variable> variables = [new(new ObjectIdentifier(oid))];
                ISnmpMessage request = version == VersionCode.V3
                    ? next
                        ? new GetNextRequestMessage(version, Messenger.NextMessageId, Messenger.NextRequestId, new OctetString(credentials.Username), OctetString.Empty, variables, privacy!, 65507, report!)
                        : new GetRequestMessage(version, Messenger.NextMessageId, Messenger.NextRequestId, new OctetString(credentials.Username), OctetString.Empty, variables, privacy!, 65507, report!)
                    : next
                        ? new GetNextRequestMessage(Messenger.NextRequestId, version, new OctetString(credentials.Community), variables)
                        : new GetRequestMessage(Messenger.NextRequestId, version, new OctetString(credentials.Community), variables);
                var response = await RetryAsync(ct => request.GetResponseAsync(endpoint, ct), token);
                if (response is ReportMessage)
                {
                    if (resync == 0 && response.Pdu().Variables.Any(v => v.Id.ToString() == "1.3.6.1.6.3.15.1.1.2.0")) { report = response; continue; }
                    throw new SnmpRejectedException();
                }
                var error = response.Pdu().ErrorStatus.ToInt32();
                if (error == 2) return null; // v1 noSuchName ends an optional table.
                if (error != 0) throw new SnmpRejectedException();
                if (version == VersionCode.V3) report = response;
                return response.Pdu().Variables.FirstOrDefault();
            }
            throw new SnmpRejectedException();
        }

        public async Task<List<Variable>> WalkAsync(string root, int limit, CancellationToken token)
        {
            var result = new List<Variable>(); var cursor = root;
            for (var i = 0; i < limit; i++)
            {
                var variable = await RequestAsync(cursor, true, token);
                if (variable is null || !Useful(variable.Data)) return result;
                var oid = variable.Id.ToString().TrimStart('.');
                if (!oid.StartsWith(root + ".", StringComparison.Ordinal)) return result;
                if (CompareOid(oid, cursor) <= 0) throw new SnmpRejectedException();
                result.Add(variable); cursor = oid;
            }
            // Never report a truncated table as a complete measurement.
            throw new SnmpRejectedException();
        }

        private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken token)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.TimeoutMs);
                try { return await call(timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < options.Retries) { }
                catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) when (!token.IsCancellationRequested && attempt < options.Retries) { }
            }
        }
    }

    public static int CompareOid(string left, string right)
    {
        var a = left.TrimStart('.').Split('.').Select(uint.Parse).ToArray();
        var b = right.TrimStart('.').Split('.').Select(uint.Parse).ToArray();
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { var cmp = a[i].CompareTo(b[i]); if (cmp != 0) return cmp; }
        return a.Length.CompareTo(b.Length);
    }
    private sealed class SnmpRejectedException : Exception;
}
