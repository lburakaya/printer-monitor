using PrinterMonitor.Simulation;

var port = args.Length > 0 ? int.Parse(args[0]) : 1161;
var scenario = args.Length > 1 ? args[1] : "normal";
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
using var simulator = new LocalSnmpSimulator(port, scenario);
Console.WriteLine($"Sanal yazıcı: 127.0.0.1:{simulator.Port} / SNMP v1 veya v2c / community: simulator-read / senaryo: {scenario}");
Console.WriteLine("Yalnızca yerel bilgisayarda dinleniyor. Durdurmak için Ctrl+C.");
try { await simulator.RunAsync(cancel.Token); } catch (OperationCanceledException) { }
