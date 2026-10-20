namespace PrinterMonitor;

public sealed class DemoPrinterAdapter : IPrinterAdapter
{
    public async Task<PrinterReading> ReadAsync(PrinterConfig config, Credentials credentials, CancellationToken cancellationToken)
    {
        await Task.Delay(180, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (config.DemoScenario == "unreachable")
            return new("unreachable", "Demo senaryosu: SNMP yanıtı alınamadı.", null, null, [], now, null);
        if (config.DemoScenario == "unknown")
            return new("online", "Cihaza ulaşıldı; sarf malzemesi tablosu desteklenmiyor.", "Demo · Mono 200", "DEMO-0004", [], now, now);
        int[] values = config.DemoScenario == "low" ? [12, 64, 38, 8] : [76, 62, 84, 51];
        string[] names = ["Siyah toner", "Cyan toner", "Magenta toner", "Sarı toner"];
        string[] colors = ["black", "cyan", "magenta", "yellow"];
        return new("online", "Demo verisi · gerçek cihaz sorgulanmadı.", "Demo · Color 450", "DEMO-" + config.Id.ToString()[..8].ToUpperInvariant(),
            Enumerable.Range(0, 4).Select(i => new Supply($"1.{i + 1}", names[i], colors[i], values[i], "measured", values[i], 100, 19, false)).ToList(), now, now);
    }
    public static void Seed(PrinterStore store)
    {
        if (store.All().Count != 0) return;
        (string Name, string Host, string Location, string Scenario)[] rows =
        [
            ("Üretim ofisi", "192.0.2.21", "A Blok · Üretim", "healthy"),
            ("Kalite kontrol", "192.0.2.34", "B Blok · Laboratuvar", "low"),
            ("Sevkiyat", "192.0.2.48", "C Blok · Depo", "unreachable"),
            ("İnsan kaynakları", "192.0.2.56", "A Blok · Yönetim", "unknown")
        ];
        foreach (var row in rows) store.Save(new PrinterInput { Name = row.Name, Host = row.Host, Location = row.Location, DemoScenario = row.Scenario }, new Credentials());
    }
}
