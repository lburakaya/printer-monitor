using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace PrinterMonitor.Simulation;

// A deterministic, loopback-only test fixture; not a general-purpose SNMP agent.
public sealed class LocalSnmpSimulator : IDisposable
{
    private readonly UdpClient udp;
    private readonly List<Variable> table = [];
    private readonly string scenario;
    public int Port => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    public int Requests { get; private set; }

    public LocalSnmpSimulator(int port = 0, string scenario = "normal")
    {
        if (scenario is not ("normal" or "low" or "unknown" or "unsupported" or "timeout" or "loop"))
            throw new ArgumentException("Senaryo: normal, low, unknown, unsupported, timeout veya loop.");
        this.scenario = scenario;
        udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        Add("1.3.6.1.2.1.1.1.0", new OctetString("PrinterMonitor UDP Simulator / Color 450"));
        Add("1.3.6.1.2.1.43.5.1.1.17.7", new OctetString("SIM-0001"));
        if (scenario != "unsupported")
        {
            string[] names = ["Black Toner", "Cyan Toner", "Magenta Toner", "Yellow Toner", "Waste Toner"];
            int[] levels = scenario == "low" ? [7, 66, 43, 12, 95] : [74, 66, 43, 91, 28];
            for (var i = 0; i < names.Length; i++)
            {
                // Non-default device index tests correct table-index handling.
                var suffix = $".7.{i + 10}";
                Add(SnmpPrinterAdapter.SuppliesRoot + ".4" + suffix, new Integer32(i == 4 ? 4 : 3));
                Add(SnmpPrinterAdapter.SuppliesRoot + ".6" + suffix, new OctetString(names[i]));
                Add(SnmpPrinterAdapter.SuppliesRoot + ".7" + suffix, new Integer32(19));
                Add(SnmpPrinterAdapter.SuppliesRoot + ".8" + suffix, new Integer32(100));
                Add(SnmpPrinterAdapter.SuppliesRoot + ".9" + suffix, new Integer32(scenario == "unknown" ? (i == 0 ? -3 : -2) : levels[i]));
            }
        }
        Add("1.3.6.1.2.1.99.1.0", new Integer32(0));
        table.Sort((a, b) => SnmpPrinterAdapter.CompareOid(a.Id.ToString(), b.Id.ToString()));
    }

    private void Add(string oid, ISnmpData value) => table.Add(new(new ObjectIdentifier(oid), value));

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var received = await udp.ReceiveAsync(token);
            Requests++;
            if (scenario == "timeout") continue;
            ISnmpMessage request;
            try { request = MessageFactory.ParseMessages(received.Buffer, new UserRegistry()).First(); }
            catch { continue; }
            if (request.Version is not (VersionCode.V1 or VersionCode.V2) || request.Community().ToString() != "simulator-read") continue;
            var next = request.Pdu() is GetNextRequestPdu;
            if (!next && request.Pdu() is not GetRequestPdu) continue; // No SET support.
            var responses = new List<Variable>();
            foreach (var asked in request.Pdu().Variables)
            {
                var match = next ? table.FirstOrDefault(v => SnmpPrinterAdapter.CompareOid(v.Id.ToString(), asked.Id.ToString()) > 0)
                    : table.FirstOrDefault(v => v.Id == asked.Id);
                if (next && scenario == "loop" && asked.Id.ToString().StartsWith(SnmpPrinterAdapter.SuppliesRoot + "."))
                    match = new Variable(asked.Id, new Integer32(10));
                responses.Add(match ?? new Variable(asked.Id, new EndOfMibView()));
            }
            var response = new ResponseMessage(request.Pdu().RequestId.ToInt32(), request.Version, request.Community(), ErrorCode.NoError, 0, responses);
            await udp.SendAsync(response.ToBytes(), received.RemoteEndPoint, token);
        }
    }
    public void Dispose() => udp.Dispose();
}
