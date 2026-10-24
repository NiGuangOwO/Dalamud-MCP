using DalamudMCP.Tools;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var time = DateTimeOffset.UtcNow.AddMinutes(-2);
HoldingContainer Container(string scope, string bag, string owner, bool available, params HoldingItem[] items) =>
    new(scope, bag, owner, owner == "player" ? null : owner, available, available ? null : "not loaded", time, items);
var state = new ItemHoldings();
state.SetCharacter(1);
var bag = Container("inventory", "Inventory1", "player", true,
    new HoldingItem(10, 10, 3, false, 0), new HoldingItem(1000010, 10, 7, true, 34), new HoldingItem(20, 20, 2, false, 1));
var armoury = Container("armoury", "ArmoryRings", "player", true, new HoldingItem(1000010, 10, 1, true, 49));
var saddle = Container("saddlebag", "SaddleBag1", "player", true, new HoldingItem(10, 10, 50, false, 30));
var retainer = Container("retainer", "RetainerPage7", "A", true, new HoldingItem(10, 10, 99, false, 24));
var roster = new[] { new HoldingRetainer("A", "A"), new HoldingRetainer("B", "B") };
string? Name(uint id) => id == 10 ? "测试道具" : id == 20 ? "另一道具" : null;
JObject Query(string json, params HoldingContainer[] current) => state.Query(JObject.Parse(json), current, roster, true, Name);
state.Observe(new[] { bag, armoury, saddle, retainer });
var all = Query("{itemId:10}", bag, armoury);
Check(all.Value<long>("totalQuantity") == 160 && all.Value<long>("hqQuantity") == 8 && all.Value<long>("nqQuantity") == 152,
    "HQ and NQ quantities aggregate across four storage scopes by base ID");
Check(!all.Value<bool>("complete") && all["retainers"]![1]!.Value<string>("source") == "unavailable", "unvisited retainers remain unknown");
Check(all["locations"]!.Any(l => l.Value<string>("ownerId") == "A" && l.Value<string>("source") == "cached"), "closed retainer retains owner and cache source");
var live = Query("{itemId:10,includeCached:false,scopes:['inventory','armoury']}", bag, armoury);
Check(live.Value<bool>("complete") && live.Value<long>("totalQuantity") == 11, "live-only scoped search excludes cached storage");
var page = Query("{name:'测试',contains:true,maxResults:1,offset:1}", bag, armoury);
Check(page.Value<long>("totalQuantity") == 160 && page.Value<int>("matchedSlots") == 5 && page.Value<int>("nextOffset") == 2,
    "paging limits locations without limiting totals");
Check(Query("{itemId:10,isHq:true}", bag, armoury).Value<long>("totalQuantity") == 8, "HQ-only filter");
Check(Query("{name:'测试'}", bag).Value<long>("totalQuantity") == 0, "exact-name default does not silently use substring search");
var unavailable = Container("saddlebag", "SaddleBag1", "player", false);
var fallback = Query("{itemId:10,scopes:['saddlebag']}", unavailable);
Check(fallback.Value<long>("totalQuantity") == 50 && !fallback.Value<bool>("complete") && fallback["coverage"]![0]!.Value<string>("source") == "cached",
    "unloaded container falls back to explicitly stale snapshot");
var noCache = Query("{itemId:10,scopes:['saddlebag'],includeCached:false}", unavailable);
Check(!noCache.Value<bool>("complete") && noCache["coverage"]![0]!["quantity"]!.Type == JTokenType.Null, "unknown storage never reports an observed zero");
state.Observe(new[] { Container("saddlebag", "SaddleBag1", "player", true) });
Check(Query("{itemId:10,scopes:['saddlebag']}", unavailable).Value<long>("totalQuantity") == 0, "observed empty storage replaces an old positive snapshot");
var a = Container("retainer", "RetainerPage7", "A", true, new HoldingItem(10, 10, 5, false, 1));
var b = Container("retainer", "RetainerPage7", "B", true, new HoldingItem(10, 10, 8, false, 1));
state.Observe(new[] { a, b });
Check(Query("{itemId:10,scopes:['retainer']}", b).Value<long>("totalQuantity") == 13, "same bag/slot on different retainers stays distinct, live replaces its own cache");
state.SetCharacter(2);
Check(Query("{itemId:10}").Value<long>("totalQuantity") == 0, "character switch clears all snapshots");
state.Observe(new[] { a }); state.SetCharacter(0); state.SetCharacter(2);
Check(Query("{itemId:10}").Value<long>("totalQuantity") == 0, "logout and relog clear snapshots even for the same character");
var missingName = Container("inventory", "Inventory1", "player", true, new HoldingItem(99, 99, 1, false, 0));
var unknown = Query("{name:'x',scopes:['inventory']}", missingName);
Check(unknown.Value<bool>("unknownItemNames") && !unknown.Value<bool>("complete"), "failed name resolution prevents a false complete absence");
state.SetCharacter(3);
state.Observe(Enumerable.Range(0, 140).Select(i => Container("retainer", "RetainerPage1", i.ToString(), true, new HoldingItem(10, 10, 1, false, 0))));
Check(Query("{itemId:10,scopes:['retainer']}").Value<int>("matchedSlots") == 128, "session cache remains bounded under retainer roster churn");
foreach (var bad in new[] { "{}", "{itemId:10,name:'x'}", "{itemId:0}", "{itemId:1.5}", "{itemId:'10'}", "{name:'  '}",
    "{itemId:10,isHq:'true'}", "{itemId:10,includeCached:1}", "{itemId:10,maxResults:201}", "{itemId:10,offset:-1}",
    "{itemId:10,scopes:[]}", "{itemId:10,scopes:['housing']}" })
{
    var refused = false; try { HoldingQuery.Parse(JObject.Parse(bad)); } catch (ToolException) { refused = true; }
    Check(refused, "reject invalid query " + bad);
}
var gate = new RetainerOwnership();
gate.Observe(100, true); Check(gate.CanRead(100), "initial loaded retainer is readable");
gate.Observe(200, true); Check(!gate.CanRead(200) && !gate.CanRead(100), "switch cannot relabel still-loaded old buffers");
gate.Observe(200, true); Check(!gate.CanRead(200), "time alone cannot establish buffer ownership");
gate.Observe(200, false); gate.Observe(200, true); Check(gate.CanRead(200), "observed unload/reload establishes new owner");
gate.Observe(0, true); gate.Observe(300, true);
Check(!gate.CanRead(300), "cleared selection cannot relabel still-loaded old buffers");
gate.Observe(300, false); gate.Observe(300, true); Check(gate.CanRead(300), "ownership resumes after real unload");
gate.Reset(); Check(!gate.CanRead(200), "reset removes owner identity");
NativeContainerTests.Run(Check);
Console.WriteLine($"{checks}/{checks} checks passed");
