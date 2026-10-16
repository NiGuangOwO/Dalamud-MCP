using System.Reflection;
using System.Runtime.InteropServices;
using DalamudMCP;

unsafe class Program
{
    [DllImport("kernel32.dll")] static extern nint VirtualAlloc(nint address,nuint size,uint type,uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(nint address,nuint size,uint type);
    static int checks;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS: "+label);}
    static int Offset(Type t,string field)=>t.GetField(field)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
    static int Size(Type t)=>t.GetField("StructSize")?.GetRawConstantValue() is int size ? size : Marshal.SizeOf(t);
    static int Main()
    {
        var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN","addon","Hooks","dev");
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var plugin=Assembly.LoadFrom(Path.Combine(repo,"src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll"));
        Console.WriteLine("Build MVID: "+plugin.ManifestModule.ModuleVersionId);
        var fcs=Assembly.LoadFrom(Path.Combine(sdk,"FFXIVClientStructs.dll"));
        Type T(string name)=>fcs.GetType("FFXIVClientStructs.FFXIV.Component.GUI."+name)!;
        var ui=plugin.GetType("DalamudMCP.Tools.UiTools")!;
        var label=ui.GetMethod("NodeLabel",BindingFlags.NonPublic|BindingFlags.Static)!;
        object? Label(nint ptr)=>label.Invoke(null,new[]{Pointer.Box((void*)ptr,label.GetParameters()[0].ParameterType)});
        Check(Label(1)==null,"invalid node pointer returns no label without access violation");
        var allocations=new List<nint>();
        nint Allocate(int size){var p=Marshal.AllocHGlobal(size);new Span<byte>((void*)p,size).Clear();allocations.Add(p);return p;}
        try
        {
            var textType=T("AtkTextNode");var nodeType=T("AtkResNode");
            var node=Allocate(Size(textType));
            var typeField=nodeType.GetField("Type")!;
            Marshal.WriteInt16(node,Offset(nodeType,"Type"),Convert.ToInt16(Enum.Parse(typeField.FieldType,"Text")));
            var utf=textType.GetField("NodeText")!.FieldType;
            var textOffset=Offset(textType,"NodeText")+Offset(utf,"StringPtr");
            Marshal.WriteIntPtr(node,textOffset,1);
            Check(Label(node)==null,"invalid text buffer pointer is refused");
            var text=Marshal.StringToCoTaskMemUTF8("guarded native label");
            try {Marshal.WriteIntPtr(node,textOffset,text);Check((string?)Label(node)=="guarded native label","readable UTF8 label preserved");}
            finally{Marshal.FreeCoTaskMem(text);}
            Marshal.WriteIntPtr(node,textOffset,0);
            Marshal.WriteIntPtr(node,Offset(nodeType,"ChildNode"),1);
            Marshal.WriteIntPtr(node,Offset(nodeType,"NextSiblingNode"),node);
            var json=Assembly.LoadFrom(Path.Combine(sdk,"Newtonsoft.Json.dll"));
            var elements=Activator.CreateInstance(json.GetType("Newtonsoft.Json.Linq.JArray")!);
            var walk=ui.GetMethod("WalkNode",BindingFlags.NonPublic|BindingFlags.Static)!;
            object?[] args={Pointer.Box((void*)node,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,100,true,elements,false,new HashSet<nuint>()};
            walk.Invoke(null,args);
            Check((int)elements!.GetType().GetProperty("Count")!.GetValue(elements)! == 1,"walker ignores invalid child and cyclic sibling links");
            var listType=T("AtkComponentList");var compNodeType=T("AtkComponentNode");var compType=T("AtkComponentBase");
            var compNode=Allocate(Size(compNodeType));var list=Allocate(Size(listType));var info=Allocate(Size(T("AtkUldComponentInfo")));
            Marshal.WriteInt16(compNode,Offset(nodeType,"Type"),1000);Marshal.WriteIntPtr(compNode,Offset(compNodeType,"Component"),list);
            var uld=compType.GetField("UldManager")!.FieldType;var uldOffset=Offset(compType,"UldManager");
            var baseType=uld.GetField("BaseType")!.FieldType;
            Marshal.WriteByte(list,uldOffset+Offset(uld,"BaseType"),Convert.ToByte(Enum.Parse(baseType,"Component")));
            Marshal.WriteIntPtr(list,uldOffset+Offset(uld,"Objects"),info);
            Marshal.WriteIntPtr(list,uldOffset+Offset(uld,"ComponentData"),1);
            var componentInfo=T("AtkUldComponentInfo");var kind=componentInfo.GetField("ComponentType")!.FieldType;
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"List")));
            var kindMethod=ui.GetMethod("ComponentKindOf",BindingFlags.NonPublic|BindingFlags.Static)!;
            var actualKind=kindMethod.Invoke(null,new[]{Pointer.Box((void*)compNode,kindMethod.GetParameters()[0].ParameterType)});
            Check(actualKind?.ToString()=="List","synthetic component actually reaches the list-label branch");
            Marshal.WriteInt32(list,Offset(listType,"ListLength"),8);Marshal.WriteIntPtr(list,Offset(listType,"ItemLabels"),1);
            Check(Label(compNode)==null,"invalid list-label array is refused without dereferencing it");
            var labels=Allocate(8*8);Marshal.WriteIntPtr(labels,1);Marshal.WriteIntPtr(list,Offset(listType,"ItemLabels"),labels);
            Check(Label(compNode)==null,"readable label array containing an invalid string pointer is refused");
            var valid=Marshal.StringToCoTaskMemUTF8("list label");
            try{Marshal.WriteIntPtr(labels,valid);Check(((string?)Label(compNode))?.Contains("list label")==true,"valid list label is read through the same branch");}finally{Marshal.FreeCoTaskMem(valid);}
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"Button")));
            Marshal.WriteIntPtr(list,Offset(T("AtkComponentButton"),"ButtonTextNode"),unchecked((nint)0x7FFF00000003L));
            Check(Label(compNode)==null,"button with noncanonical text-node pointer cannot fault before string validation");
        }
        finally{foreach(var p in allocations)Marshal.FreeHGlobal(p);}
        var page=VirtualAlloc(0,4096,0x3000,0x04);
        if(page==0)throw new Exception("VirtualAlloc failed");
        try
        {
            Marshal.WriteInt64(page,123456789);
            Check(MemoryProbe.TryReadStruct<ulong>((ulong)page,out _)==123456789,"complete native scalar read succeeds");
            Check(MemoryProbe.TryReadStruct<ulong>((ulong)page+4092,out var error)==null&&error!.Contains("incomplete"),"partial page-end struct refused before unmarshalling");
            Check(MemoryProbe.TryReadStruct<ulong>(1,out _)==null,"unmapped native struct refused");
        }
        finally{VirtualFree(page,0,0x8000);}
        Console.WriteLine($"{checks}/{checks} checks passed");return 0;
    }
}
