using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

internal static class CombatEffectJson
{
    public static JObject Decode(byte type, byte p0, byte p1, byte p2, byte p3, byte p4, ushort value)
    {
        var data = new JObject { ["effectType"] = type, ["param0"] = p0, ["param1"] = p1,
            ["param2"] = p2, ["param3"] = p3, ["param4"] = p4, ["value"] = value,
            ["atSource"] = (p4 & 0x80) != 0, ["fromTarget"] = (p4 & 0x20) != 0 };
        if (type is 3 or 4 or 5 or 6)
        {
            data["kind"] = type == 4 ? "heal" : "damage";
            data["amount"] = value + ((p4 & 0x40) != 0 ? p3 * 65536 : 0);
            data["critical"] = (p0 & 0x20) != 0;
            data["directHit"] = (p0 & 0x40) != 0;
        }
        return data;
    }
}
