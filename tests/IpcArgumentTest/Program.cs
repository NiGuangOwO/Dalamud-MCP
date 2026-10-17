using System.Reflection;
var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN","addon","Hooks","dev");
AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var p=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var plugin=Assembly.LoadFrom(Path.Combine(repo,"src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll"));
var type=plugin.GetType("DalamudMCP.Ipc.PluginIpcInvoker")!;
var endpoint=Activator.CreateInstance(plugin.GetType("DalamudMCP.Ipc.IpcEndpoint")!)!;
var read=type.GetMethod("ReadValue",BindingFlags.NonPublic|BindingFlags.Static)!;
object? Read<T>(string json)=>read.MakeGenericMethod(typeof(T)).Invoke(null,new[]{endpoint,json});
bool Refused(Action call){try{call();return false;}catch(TargetInvocationException ex)when(ex.InnerException is ArgumentException){return true;}}
var count=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;Console.WriteLine("PASS: "+label);}
Check(Equals(Read<bool>("true"),true)&&Equals(Read<bool>("false"),false),"native JSON booleans accepted");
Check(Refused(()=>Read<bool>("\"false\"")),"string boolean cannot silently coerce");
Check(Refused(()=>Read<bool>("1")),"integer cannot silently toggle a boolean endpoint");
Check(Refused(()=>Read<bool>("null")),"null does not become false");
Check(Equals(Read<int>("42"),42),"native JSON integer accepted");
Check(Refused(()=>Read<int>("\"42\"")),"string integer cannot silently coerce");
Check(Refused(()=>Read<int>("4.2")),"float cannot silently truncate");
Check(Refused(()=>Read<int>("2147483648")),"Int32 overflow refused");
Check(Equals(Read<string>("\"AutoOutOnALimb\""),"AutoOutOnALimb"),"native JSON string accepted without changing module key");
Check(Refused(()=>Read<string>("true"))&&Refused(()=>Read<string>("42")),"boolean and integer cannot silently become string query arguments");
Check(Refused(()=>Read<string>("null")),"null string refused");
Check(Refused(()=>Read<string>("{\"module\":\"AutoOutOnALimb\"}")),"object cannot coerce into module name");
Check(Equals(Read<string>("\""+new string('a',4096)+"\""),new string('a',4096)),"bounded string limit accepted");
Check(Refused(()=>Read<string>("\""+new string('a',4097)+"\"")),"oversized IPC string refused");
var normalize=plugin.GetType("DalamudMCP.Ipc.IpcSignatures")!.GetMethod("Normalize")!;
foreach(var signature in new[]{"Func<string, bool>","Func<string, bool?>","Func<string, string>"})
 Check(Equals(normalize.Invoke(null,new[]{signature}),signature),signature+" is explicitly registered");
var invoke=type.GetMethod("Invoke")!;
foreach(var signature in new[]{"Func<bool>","Func<string>"}){
 endpoint.GetType().GetProperty("Signature")!.SetValue(endpoint,signature);
 Check(Refused(()=>invoke.Invoke(null,new[]{null,endpoint,"false"})),signature+" rejects unexpected value before contacting IPC");
}
Console.WriteLine($"{count}/{count} checks passed");
