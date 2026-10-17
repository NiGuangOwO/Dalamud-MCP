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
            // Reproduce the live SelectString layout: the list is in the ULD array,
            // while the legacy child/sibling path does not expose it.
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"List")));
            var flagType=nodeType.GetField("NodeFlags")!.FieldType;
            Marshal.WriteInt32(compNode,Offset(nodeType,"NodeFlags"),Convert.ToInt32(Enum.Parse(flagType,"Visible"))|Convert.ToInt32(Enum.Parse(flagType,"Enabled")));
            Marshal.WriteIntPtr(list,Offset(compType,"OwnerNode"),compNode);
            Marshal.WriteInt32(list,Offset(listType,"ListLength"),2);
            var unitType=T("AtkUnitBase"); var selectType=fcs.GetType("FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString")!;
            var unit=Allocate(Size(selectType));var nodeArray=Allocate(8);
            Marshal.WriteIntPtr(nodeArray,compNode);
            var unitUld=Offset(unitType,"UldManager");
            Marshal.WriteIntPtr(unit,unitUld+Offset(uld,"NodeList"),nodeArray);
            Marshal.WriteInt16(unit,unitUld+Offset(uld,"NodeListCount"),1);
            Marshal.WriteIntPtr(unit,Offset(unitType,"RootNode"),1);
            var findList=ui.GetMethod("FindListByIndex",BindingFlags.NonPublic|BindingFlags.Static)!;
            nint FindList(int index)=>(nint)Pointer.Unbox(findList.Invoke(null,new object?[]{Pointer.Box((void*)unit,findList.GetParameters()[0].ParameterType),index})!);
            Check(FindList(0)==list,"list discovery uses ULD array without dereferencing invalid root");
            Check(FindList(-1)==0&&FindList(2)==0,"negative and out-of-range list indices refused");
            Marshal.WriteIntPtr(unit,unitUld+Offset(uld,"NodeList"),1);
            Check(FindList(0)==0,"unreadable ULD node array refused");
            Marshal.WriteIntPtr(unit,unitUld+Offset(uld,"NodeList"),nodeArray);
            var popupType=selectType.GetField("PopupMenu")!.FieldType;var popupOffset=Offset(selectType,"PopupMenu");
            Marshal.WriteIntPtr(unit,popupOffset+Offset(popupType,"Owner"),unit);
            Marshal.WriteIntPtr(unit,popupOffset+Offset(popupType,"List"),list);
            Marshal.WriteInt32(unit,popupOffset+Offset(popupType,"EntryCount"),2);
            var entryNames=Allocate(16);Marshal.WriteIntPtr(unit,popupOffset+Offset(popupType,"EntryNames"),entryNames);
            var label0=Marshal.StringToCoTaskMemUTF8("Aethernet");var label1=Marshal.StringToCoTaskMemUTF8("Cancel");
            try
            {
                Marshal.WriteIntPtr(entryNames,label0);Marshal.WriteIntPtr(entryNames,8,label1);
                var readMenu=ui.GetMethod("ReadSelectStringEntries",BindingFlags.NonPublic|BindingFlags.Static)!;
                object? ReadMenu()=>readMenu.Invoke(null,new object?[]{Pointer.Box((void*)unit,readMenu.GetParameters()[0].ParameterType),(nint)0});
                var entries=(System.Collections.IList)ReadMenu()!;
                var first=(System.Runtime.CompilerServices.ITuple)entries[0]!;
                Check(entries.Count==2&&(int)first[0]! == 0&&(string)first[1]! == "Aethernet","popup menu uses ordinal zero and excludes title/value indices");
                bool Refused(){try{ReadMenu();return false;}catch(TargetInvocationException e){return e.InnerException?.GetType().Name=="ToolException";}}
                Marshal.WriteInt32(compNode,Offset(nodeType,"NodeFlags"),Convert.ToInt32(Enum.Parse(flagType,"Visible")));
                Check(Refused(),"disabled popup list refused");
                Marshal.WriteInt32(compNode,Offset(nodeType,"NodeFlags"),Convert.ToInt32(Enum.Parse(flagType,"Visible"))|Convert.ToInt32(Enum.Parse(flagType,"Enabled")));
                Marshal.WriteInt32(unit,popupOffset+Offset(popupType,"EntryCount"),257);
                Check(Refused(),"oversized popup entry count refused");
                Marshal.WriteInt32(unit,popupOffset+Offset(popupType,"EntryCount"),2);
                Marshal.WriteIntPtr(entryNames,1);
                Check(Refused(),"unreadable popup label refused before native selection");
                Marshal.WriteIntPtr(entryNames,label0);Marshal.WriteIntPtr(unit,popupOffset+Offset(popupType,"Owner"),1);
                Check(Refused(),"popup belonging to a different addon refused");
            }
            finally{Marshal.FreeCoTaskMem(label0);Marshal.FreeCoTaskMem(label1);}
            var tableType=Assembly.LoadFrom(Path.Combine(sdk,"Dalamud.dll")).GetType("Dalamud.Game.Addon.Lifecycle.AddonVirtualTable")!;
            var table=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(tableType);
            var tableFlags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            void SetPointer(string name,nint pointer){var field=tableType.GetField(name,tableFlags)!;field.SetValue(table,Pointer.Box((void*)pointer,field.FieldType));}
            var modified=Allocate(32);var original=Allocate(32);
            SetPointer("atkUnitBase",unit);SetPointer("<ModifiedVirtualTable>k__BackingField",modified);SetPointer("<OriginalVirtualTable>k__BackingField",original);
            var dispatch=plugin.GetType("DalamudMCP.Tools.AddonDispatchTools")!.GetMethod("FindOriginal",BindingFlags.NonPublic|BindingFlags.Static)!;
            nint FindOriginal(System.Collections.IEnumerable entries,nint owner,nint current)=>(nint)dispatch.Invoke(null,new object?[]{entries,owner,current})!;
            Check(FindOriginal(new object?[]{null,"unsupported",table},unit,modified)==original,"original dispatch uses exact SDK table and matching owner/current table");
            Check(FindOriginal(new[]{table},(nint)1,modified)==0,"unrelated addon lifecycle table refused");
            Check(FindOriginal(new[]{table},unit,(nint)1)==0,"stale or replaced modified vtable refused");
            Check(FindOriginal(Array.Empty<object>(),unit,modified)==0,"empty lifecycle registry returns unavailable");
            var many=Enumerable.Repeat<object?>(null,1025).ToArray();many[1024]=table;
            Check(FindOriginal(many,unit,modified)==0,"lifecycle table search bounded to 1024 entries");
            many[1023]=table;Check(FindOriginal(many,unit,modified)==original,"last in-budget lifecycle table is discoverable");
            SetPointer("<OriginalVirtualTable>k__BackingField",0);
            Check(FindOriginal(new[]{table},unit,modified)==0,"missing original table returns unavailable without native invocation");
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
