using System.Reflection;
using DalamudMCP.Tools;

var assembly = Assembly.GetExecutingAssembly();
var expected = assembly.ManifestModule.ModuleVersionId;
var actual = DevBuildIdentity.Read(assembly.Location);
if (actual != expected || actual == Guid.Empty) throw new Exception("PE metadata identity does not match loaded test assembly");
Console.WriteLine("PASS: actual PE metadata MVID matches loaded assembly without loading the file again");
var copy = Path.Combine(Path.GetTempPath(), $"mcp-build-identity-{Guid.NewGuid():N}.dll");
try
{
    File.Copy(assembly.Location, copy);
    if (DevBuildIdentity.Read(copy) != actual) throw new Exception("renaming/copying changed build identity");
    Console.WriteLine("PASS: build identity is independent of the file path");
    File.WriteAllText(copy, "incomplete DLL write");
    try { DevBuildIdentity.Read(copy); throw new Exception("invalid PE was accepted"); }
    catch (BadImageFormatException) { Console.WriteLine("PASS: partial/non-PE build output is rejected"); }
}
finally { File.Delete(copy); }
Console.WriteLine("3/3 build identity checks passed");
