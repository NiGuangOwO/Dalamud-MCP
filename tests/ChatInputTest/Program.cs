using System.Reflection;
var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN","addon","Hooks","dev");
AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var p=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var assembly=Assembly.LoadFrom(Path.Combine(repo,"src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll"));
var method=assembly.GetType("DalamudMCP.Tools.ChatTools")!.GetMethod("ValidateChatBody",BindingFlags.Static|BindingFlags.NonPublic)!;
var count=0;
void Check(string text, bool accepted){
 bool actual=true;
 try{method.Invoke(null,new object[]{text});}
 catch(TargetInvocationException e) when(e.InnerException?.GetType().FullName=="DalamudMCP.Mcp.ToolException"){actual=false;}
 if(actual!=accepted)throw new Exception("Unexpected chat validation: "+text);
 count++; Console.WriteLine("PASS: input case "+count);
}
Check("/cts limb 1",false);
Check("  /cts status",false);
Check("\t/shutdown",false);
Check("普通消息",true);
Check("read/write",true);
Check("第一行\n第二行",false);
Check("first\rsecond",false);
Check("nul\0body",false);
Console.WriteLine($"{count}/{count} checks passed; no chat or game input dispatched");
