using System.Reflection;
using System.Runtime.InteropServices;

var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "addon", "Hooks", "dev");
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var path = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var plugin = Assembly.LoadFrom(Path.Combine(repo, "src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll"));
var scan = plugin.GetType("DalamudMCP.Tools.MemoryTools")!.GetMethod("ScanRangeBounded", BindingFlags.NonPublic | BindingFlags.Static)!;
var bytes = new byte[] { 0xAB, 0xAB, 0xAB, 0x10, 0x20, 0xAB, 0xAB, 0x30, 0x40 };
var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
var count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
try
{
    var address = pinned.AddrOfPinnedObject();
    long[] Scan(int offset, int size, string pattern, int limit) =>
        ((IEnumerable<IntPtr>)scan.Invoke(null, new object[] { address + offset, size, pattern, limit })!)
        .Select(p => p.ToInt64() - address.ToInt64()).ToArray();
    Check(Scan(0, bytes.Length, "AB AB", 10).SequenceEqual(new long[] { 0, 1, 5 }), "overlapping matches preserved");
    Check(Scan(0, bytes.Length, "AB AB", 1).SequenceEqual(new long[] { 0 }), "single-result bound");
    Check(Scan(0, bytes.Length, "AB AB", 2).SequenceEqual(new long[] { 0, 1 }), "lookahead bound for truncated results");
    Check(Scan(3, 6, "AB AB", 10).SequenceEqual(new long[] { 5 }), "selected section excludes earlier matches");
    Check(Scan(3, 2, "AB AB", 10).Length == 0, "selected section excludes later matches");
    Check(Scan(0, bytes.Length, "30 40", 10).SequenceEqual(new long[] { 7 }), "pattern ending at section boundary");
    Check(Scan(0, bytes.Length, "10 ?? AB", 10).SequenceEqual(new long[] { 3 }), "SDK wildcard matching retained");
    Check(Scan(0, bytes.Length, "FE ED", 10).Length == 0, "absent pattern returns empty");
    Check(Scan(0, 0, "AB", 10).Length == 0, "empty range is not read");
}
finally { pinned.Free(); }
Console.WriteLine($"{count}/{count} checks passed");
