using System.Reflection;
using System.Runtime.InteropServices;
using DalamudMCP;

unsafe class Program
{
    [DllImport("kernel32.dll")] static extern nint VirtualAlloc(nint address,nuint size,uint type,uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(nint address,nuint size,uint type);
    static int checks;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS: "+label);}
    static int Offset(Type t,string field)=>t.GetField(field,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
    static int Size(Type t)=>t.GetField("StructSize")?.GetRawConstantValue() is int size ? size : Marshal.SizeOf(t);
    static int Main(string[] cliArgs)
    {
        var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN","addon","Hooks","dev");
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var plugin=Assembly.LoadFrom(cliArgs.Length>0?Path.GetFullPath(cliArgs[0]):Path.Combine(repo,"src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll"));
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
            var queryType=plugin.GetType("DalamudMCP.Tools.AddonElementQuery")!;
            var jtype=json.GetType("Newtonsoft.Json.Linq.JObject")!;var parse=jtype.GetMethod("Parse",new[]{typeof(string)})!;
            object Query(string input="{}",int max=100)=>Activator.CreateInstance(queryType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object?[]{parse.Invoke(null,new[]{input}),max},null)!;
            object Elements(object query)=>queryType.GetProperty("Elements",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(query)!;
            int Count(object query)=>(int)Elements(query).GetType().GetProperty("Count")!.GetValue(Elements(query))!;
            var query=Query();
            var walk=ui.GetMethod("WalkNode",BindingFlags.NonPublic|BindingFlags.Static)!;
            object?[] args={Pointer.Box((void*)node,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,true,query,Array.Empty<uint>(),true};
            walk.Invoke(null,args);
            Check(Count(query)==1,"walker ignores invalid child and cyclic sibling links");
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
            var visibilityApi=plugin.GetType("DalamudMCP.Tools.NativeUiVisibility")!;
            var readVisibility=visibilityApi.GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static)!;
            bool? Visible(nint p)=>(bool?)readVisibility.Invoke(null,new object?[]{p,null});
            var visibleFlag=Convert.ToInt32(Enum.Parse(flagType,"Visible"));
            var vchild=Allocate(Size(nodeType));var vparent=Allocate(Size(nodeType));
            Marshal.WriteInt32(vchild,Offset(nodeType,"NodeFlags"),visibleFlag);
            Marshal.WriteInt32(vparent,Offset(nodeType,"NodeFlags"),visibleFlag);
            Marshal.WriteIntPtr(vchild,Offset(nodeType,"ParentNode"),vparent);
            Check(Visible(vchild)==true,"visible child under visible parent is effectively visible");
            Marshal.WriteInt32(vparent,Offset(nodeType,"NodeFlags"),0);
            Check(Visible(vchild)==false,"self-visible child under hidden panel is effectively hidden");
            Marshal.WriteIntPtr(vparent,Offset(nodeType,"ParentNode"),1);
            Check(Visible(vchild)==false,"known hidden ancestor stays hidden even if its own parent is unreadable");
            Marshal.WriteInt32(vparent,Offset(nodeType,"NodeFlags"),visibleFlag);
            Check(Visible(vchild)==null,"unreadable visible ancestry is unknown rather than visible");
            Marshal.WriteIntPtr(vparent,Offset(nodeType,"ParentNode"),vchild);
            Check(Visible(vchild)==null,"cyclic visible ancestry is unknown without looping");
            Check(Visible(0)==null&&Visible(1)==null,"null and unmapped visibility pointers are unknown");
            var chain=Enumerable.Range(0,65).Select(_=>Allocate(Size(nodeType))).ToArray();
            for(var i=0;i<chain.Length;i++){Marshal.WriteInt32(chain[i],Offset(nodeType,"NodeFlags"),visibleFlag);if(i+1<chain.Length)Marshal.WriteIntPtr(chain[i],Offset(nodeType,"ParentNode"),chain[i+1]);}
            Check(Visible(chain[1])==true&&Visible(chain[0])==null,"visibility accepts 64 ancestors and bounds longer chains");
            var combine=visibilityApi.GetMethod("Combine",BindingFlags.NonPublic|BindingFlags.Static)!;
            bool? Combined(bool? a,bool? b)=>(bool?)combine.Invoke(null,new object?[]{a,b});
            Check(Combined(false,null)==false&&Combined(null,false)==false&&Combined(true,null)==null&&Combined(true,true)==true,"scope visibility preserves hidden and unknown states");
            Marshal.WriteInt32(vchild,Offset(nodeType,"NodeId"),9001);
            Marshal.WriteIntPtr(vchild,Offset(nodeType,"ParentNode"),vparent);
            Marshal.WriteIntPtr(vparent,Offset(nodeType,"ParentNode"),0);
            Marshal.WriteInt32(vparent,Offset(nodeType,"NodeFlags"),0);
            var hiddenQuery=Query("{\"visibleOnly\":true}");
            walk.Invoke(null,new object?[]{Pointer.Box((void*)vchild,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,true,hiddenQuery,Array.Empty<uint>(),true});
            Check(Count(hiddenQuery)==0,"visibleOnly query excludes a self-visible child beneath a hidden panel");
            var rawHiddenQuery=Query();
            walk.Invoke(null,new object?[]{Pointer.Box((void*)vchild,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,true,rawHiddenQuery,Array.Empty<uint>(),true});
            var rawElements=Elements(rawHiddenQuery);var firstHidden=rawElements.GetType().GetProperty("Item",new[]{typeof(int)})!.GetValue(rawElements,new object[]{0})!;
            var fieldItem=firstHidden.GetType().GetProperty("Item",new[]{typeof(string)})!;
            Check(fieldItem.GetValue(firstHidden,new object[]{"visible"})!.ToString()=="False"&&fieldItem.GetValue(firstHidden,new object[]{"selfVisible"})!.ToString()=="True","full query distinguishes effective visibility from the local flag");
            Marshal.WriteInt32(vparent,Offset(nodeType,"NodeFlags"),visibleFlag);
            Marshal.WriteIntPtr(vparent,Offset(nodeType,"ParentNode"),1);
            var unknownQuery=Query("{\"visibleOnly\":true}");
            walk.Invoke(null,new object?[]{Pointer.Box((void*)vchild,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,true,unknownQuery,Array.Empty<uint>(),true});
            Check(Count(unknownQuery)==0&&(bool)queryType.GetProperty("ScanIncomplete",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(unknownQuery)!,"unknown ancestry is excluded and marks the query incomplete");
            Marshal.WriteIntPtr(vchild,Offset(nodeType,"ParentNode"),0);
            var detachedQuery=Query("{\"visibleOnly\":true}");
            walk.Invoke(null,new object?[]{Pointer.Box((void*)vchild,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,true,detachedQuery,new uint[]{42},false});
            Check(Count(detachedQuery)==0,"detached component subtree inherits hidden owner scope");
            var unknownScopeQuery=Query("{\"visibleOnly\":true}");
            walk.Invoke(null,new object?[]{Pointer.Box((void*)vchild,walk.GetParameters()[0].ParameterType),Pointer.Box(null,walk.GetParameters()[1].ParameterType),0,12,true,unknownScopeQuery,new uint[]{42},null});
            Check(Count(unknownScopeQuery)==0&&(bool)queryType.GetProperty("ScanIncomplete",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(unknownScopeQuery)!,"detached component subtree preserves unknown owner visibility");
            var textApi=plugin.GetType("DalamudMCP.Tools.TextInputTools")!;
            var readInput=textApi.GetMethod("TryReadInput",BindingFlags.NonPublic|BindingFlags.Static)!;
            bool ReadInput(nint p,nint owner,out string? value){object?[] a={p,owner,(nint)0,null};var ok=(bool)readInput.Invoke(null,a)!;value=(string?)a[3];return ok;}
            var textInput=Allocate(Size(T("AtkComponentTextInput")));
            Marshal.WriteIntPtr(compNode,Offset(compNodeType,"Component"),textInput);
            Marshal.WriteByte(textInput,uldOffset+Offset(uld,"BaseType"),Convert.ToByte(Enum.Parse(baseType,"Component")));
            Marshal.WriteIntPtr(textInput,uldOffset+Offset(uld,"Objects"),info);
            Marshal.WriteIntPtr(textInput,Offset(compType,"OwnerNode"),compNode);
            Marshal.WriteIntPtr(textInput,Offset(T("AtkComponentTextInput"),"OwnerAddon"),unit);
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"TextInput")));
            var nativeText=Marshal.StringToCoTaskMemUTF8("枫木");
            var stringOffset=Offset(T("AtkComponentTextInput"),"EvaluatedString")+Offset(utf,"StringPtr");
            try
            {
                Marshal.WriteIntPtr(textInput,stringOffset,nativeText);
                Check(ReadInput(compNode,unit,out var value)&&value=="枫木","native TextInput reads actual evaluated Chinese text");
                Check(!ReadInput(compNode,(nint)1,out _),"text input belonging to another addon refused");
                Marshal.WriteIntPtr(textInput,Offset(compType,"OwnerNode"),1);
                Check(!ReadInput(compNode,unit,out _),"text input with mismatched owner node refused");
                Marshal.WriteIntPtr(textInput,Offset(compType,"OwnerNode"),compNode);
                Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"NumericInput")));
                Check(!ReadInput(compNode,unit,out _),"numeric input cannot be cast to TextInput");
                Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"TextInput")));
                Marshal.WriteIntPtr(textInput,stringOffset,1);
                Check(!ReadInput(compNode,unit,out _),"invalid evaluated string pointer refused before native invocation");
                Marshal.WriteIntPtr(compNode,Offset(compNodeType,"Component"),1);
                Check(!ReadInput(compNode,unit,out _),"unreadable text component refused");
                Check(!ReadInput(1,unit,out _),"unmapped text node refused");
            }
            finally{Marshal.FreeCoTaskMem(nativeText);}
            var usable=textApi.GetMethod("IsUsable",BindingFlags.NonPublic|BindingFlags.Static)!;
            bool Usable(nint p)=>(bool)usable.Invoke(null,new object[]{p})!;
            Marshal.WriteIntPtr(compNode,Offset(nodeType,"ParentNode"),0);
            Check(Usable(compNode),"visible enabled control is usable");
            Marshal.WriteIntPtr(compNode,Offset(nodeType,"ParentNode"),compNode);
            Check(!Usable(compNode),"cyclic text input ancestry refused");
            Marshal.WriteIntPtr(compNode,Offset(nodeType,"ParentNode"),1);
            Check(!Usable(compNode),"unreadable text input ancestry refused");
            Marshal.WriteIntPtr(compNode,Offset(nodeType,"ParentNode"),0);
            Marshal.WriteInt32(compNode,Offset(nodeType,"NodeFlags"),Convert.ToInt32(Enum.Parse(flagType,"Visible")));
            Check(!Usable(compNode),"disabled text input refused");
            Marshal.WriteInt32(compNode,Offset(nodeType,"NodeFlags"),Convert.ToInt32(Enum.Parse(flagType,"Enabled")));
            Check(!Usable(compNode),"hidden text input refused");
            Marshal.WriteInt32(compNode,Offset(nodeType,"NodeFlags"),Convert.ToInt32(Enum.Parse(flagType,"Visible"))|Convert.ToInt32(Enum.Parse(flagType,"Enabled")));
            var plain=textApi.GetMethod("PlainText",BindingFlags.NonPublic|BindingFlags.Static)!;
            object JsonArg(string input)=>parse.Invoke(null,new[]{input})!;
            string Plain(object a)=>(string)plain.Invoke(null,new object[]{a,"text"})!;
            bool RejectPlain(object a){try{Plain(a);return false;}catch(TargetInvocationException e){return e.InnerException?.GetType().Name=="ToolException";}}
            object TextArg(string text){var a=JsonArg("{}");jtype.GetProperty("Item",new[]{typeof(string)})!.SetValue(a,Activator.CreateInstance(json.GetType("Newtonsoft.Json.Linq.JValue")!,new object[]{text}),new object[]{"text"});return a;}
            Check(Plain(TextArg(""))=="","empty text explicitly clears native input");
            Check(Plain(TextArg(new string('木',85))).Length==85,"255 UTF8 bytes of Chinese accepted");
            Check(RejectPlain(TextArg(new string('木',86))),"258 UTF8 bytes of Chinese refused");
            Check(Plain(TextArg(new string('a',256))).Length==256,"256 ASCII bytes accepted");
            Check(RejectPlain(TextArg(new string('a',257))),"oversized ASCII text refused");
            Check(RejectPlain(TextArg("a\0b"))&&RejectPlain(TextArg("a\nb"))&&RejectPlain(TextArg("\u0002")),"NUL newline and SeString control payloads refused");
            Check(RejectPlain(TextArg("\u2028"))&&RejectPlain(TextArg("\u2029")),"Unicode line and paragraph separators refused");
            Check(RejectPlain(TextArg("\uD800")),"invalid Unicode surrogate refused");
            Check(RejectPlain(JsonArg("{\"text\":123}"))&&RejectPlain(JsonArg("{}")),"non-string and missing native text refused");
            var checkboxApi=plugin.GetType("DalamudMCP.Tools.CheckboxTools")!;
            var readCheckbox=checkboxApi.GetMethod("TryReadCheckbox",BindingFlags.NonPublic|BindingFlags.Static)!;
            bool ReadCheckbox(nint pointer,out bool checkedState){object?[] values={pointer,(nint)0,false};var result=(bool)readCheckbox.Invoke(null,values)!;checkedState=(bool)values[2]!;return result;}
            var checkbox=Allocate(Size(T("AtkComponentCheckBox")));
            Marshal.WriteIntPtr(compNode,Offset(compNodeType,"Component"),checkbox);
            Marshal.WriteByte(checkbox,uldOffset+Offset(uld,"BaseType"),Convert.ToByte(Enum.Parse(baseType,"Component")));
            Marshal.WriteIntPtr(checkbox,uldOffset+Offset(uld,"Objects"),info);
            Marshal.WriteIntPtr(checkbox,Offset(compType,"OwnerNode"),compNode);
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"CheckBox")));
            Check(ReadCheckbox(compNode,out var checkedState)&&!checkedState,"unchecked SDK checkbox read without native invocation");
            Marshal.WriteInt32(checkbox,Offset(T("AtkComponentButton"),"Flags"),1<<18);
            Check(ReadCheckbox(compNode,out checkedState)&&checkedState,"checked state follows installed SDK button flag");
            Marshal.WriteIntPtr(checkbox,Offset(compType,"OwnerNode"),1);
            Check(!ReadCheckbox(compNode,out _),"checkbox with mismatched owner refused");
            Marshal.WriteIntPtr(checkbox,Offset(compType,"OwnerNode"),compNode);
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"Button")));
            Check(!ReadCheckbox(compNode,out _),"ordinary button cannot become checkbox by cast");
            Marshal.WriteByte(info,Offset(componentInfo,"ComponentType"),Convert.ToByte(Enum.Parse(kind,"CheckBox")));
            Marshal.WriteIntPtr(compNode,Offset(compNodeType,"Component"),1);
            Check(!ReadCheckbox(compNode,out _),"unreadable checkbox component refused");
            Check(!ReadCheckbox(1,out _),"unmapped checkbox node refused");
            Marshal.WriteIntPtr(compNode,Offset(compNodeType,"Component"),checkbox);
            var findCheckbox=checkboxApi.GetMethod("Find",BindingFlags.NonPublic|BindingFlags.Static)!;
            nint FindCheckbox(uint id)=>(nint)Pointer.Unbox(findCheckbox.Invoke(null,new object?[]{Marshal.PtrToStructure(unit+unitUld,uld),id})!);
            Marshal.WriteInt32(compNode,Offset(nodeType,"NodeId"),3);
            Check(FindCheckbox(3)==compNode&&FindCheckbox(4)==0,"checkbox node search uses exact scoped ULD ids");
            Marshal.WriteInt16(unit,unitUld+Offset(uld,"NodeListCount"),4097);
            Check(FindCheckbox(3)==0,"oversized checkbox ULD array refused");
            Marshal.WriteInt16(unit,unitUld+Offset(uld,"NodeListCount"),1);
            Marshal.WriteIntPtr(unit,unitUld+Offset(uld,"NodeList"),1);
            Check(FindCheckbox(3)==0,"unmapped checkbox ULD array refused");
            var repeated=Allocate(16);Marshal.WriteIntPtr(repeated,compNode);Marshal.WriteIntPtr(repeated,8,compNode);
            Marshal.WriteIntPtr(unit,unitUld+Offset(uld,"NodeList"),repeated);
            Marshal.WriteInt16(unit,unitUld+Offset(uld,"NodeListCount"),2);
            Check(FindCheckbox(3)==0,"ambiguous checkbox node ids refused");
            var boolean=checkboxApi.GetMethod("StrictBoolean",BindingFlags.NonPublic|BindingFlags.Static)!;
            bool Bool(string input)=> (bool)boolean.Invoke(null,new object?[]{parse.Invoke(null,new[]{input}),"checked",null})!;
            Check(Bool("{\"checked\":true}")&&!Bool("{\"checked\":false}"),"checkbox accepts native JSON booleans");
            foreach(var input in new[]{"{}","{\"checked\":null}","{\"checked\":1}","{\"checked\":\"false\"}"}){
                var refused=false;try{Bool(input);}catch(TargetInvocationException e){refused=e.InnerException?.GetType().Name=="ToolException";}
                Check(refused,"checkbox missing/coerced boolean refused: "+input);
            }
            // Child IDs resolve by their discovered ULD path, never by a global fallback.
            var probe=plugin.GetType("DalamudMCP.Tools.AddonProbeTools")!;
            var findNode=probe.GetMethod("FindNode",BindingFlags.NonPublic|BindingFlags.Static)!;
            nint Resolve(uint id,uint[] path)=>(nint)Pointer.Unbox(findNode.Invoke(null,new object?[]{Pointer.Box((void*)unit,findNode.GetParameters()[0].ParameterType),id,null,path})!);
            Marshal.WriteIntPtr(unit,unitUld+Offset(uld,"NodeList"),nodeArray);
            Marshal.WriteInt16(unit,unitUld+Offset(uld,"NodeListCount"),1);
            Marshal.WriteInt32(node,Offset(nodeType,"NodeId"),2);
            var children=Allocate(8);Marshal.WriteIntPtr(children,node);
            Marshal.WriteIntPtr(checkbox,uldOffset+Offset(uld,"NodeList"),children);
            Marshal.WriteInt16(checkbox,uldOffset+Offset(uld,"NodeListCount"),1);
            Check(Resolve(3,Array.Empty<uint>())==compNode&&Resolve(2,new uint[]{3})==node,"scoped resolver uses exact top-level and component ULD arrays");
            var innerNode=Allocate(Size(compNodeType));var inner=Allocate(Size(compType));
            Marshal.WriteInt16(innerNode,Offset(nodeType,"Type"),1000);
            Marshal.WriteInt32(innerNode,Offset(nodeType,"NodeId"),5);
            Marshal.WriteIntPtr(innerNode,Offset(compNodeType,"Component"),inner);
            Marshal.WriteIntPtr(inner,Offset(compType,"OwnerNode"),innerNode);
            var innerChildren=Allocate(8);Marshal.WriteIntPtr(innerChildren,node);
            Marshal.WriteIntPtr(inner,uldOffset+Offset(uld,"NodeList"),innerChildren);
            Marshal.WriteInt16(inner,uldOffset+Offset(uld,"NodeListCount"),1);
            Marshal.WriteIntPtr(children,innerNode);
            Check(Resolve(2,new uint[]{3,5})==node&&Resolve(2,new uint[]{3})==0,"nested component path prevents a same-id fallback into another scope");
            var scopedQuery=Query("{\"allNodes\":true,\"detail\":\"compact\"}");
            walk.Invoke(null,new object?[]{Pointer.Box((void*)compNode,walk.GetParameters()[0].ParameterType),Pointer.Box((void*)unit,walk.GetParameters()[1].ParameterType),0,12,true,scopedQuery,Array.Empty<uint>(),true});
            Check(Elements(scopedQuery).ToString()!.Replace(" ","").Replace("\r","").Replace("\n","").Contains("\"componentPath\":[3,5]"),"native discovery preserves the full nested component path");
            var limitedQuery=Query();
            walk.Invoke(null,new object?[]{Pointer.Box((void*)compNode,walk.GetParameters()[0].ParameterType),Pointer.Box((void*)unit,walk.GetParameters()[1].ParameterType),0,0,true,limitedQuery,Array.Empty<uint>(),true});
            Check((bool)queryType.GetProperty("ScanIncomplete",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(limitedQuery)!&&Count(limitedQuery)==1,"depth-limited native traversal explicitly reports incomplete coverage");
            Marshal.WriteIntPtr(inner,Offset(compType,"OwnerNode"),1);
            Check(Resolve(2,new uint[]{3,5})==0,"component path refuses mismatched native ownership");
            Marshal.WriteIntPtr(inner,Offset(compType,"OwnerNode"),innerNode);
            var duplicated=Allocate(16);Marshal.WriteIntPtr(duplicated,node);Marshal.WriteIntPtr(duplicated,8,node);
            Marshal.WriteIntPtr(inner,uldOffset+Offset(uld,"NodeList"),duplicated);
            Marshal.WriteInt16(inner,uldOffset+Offset(uld,"NodeListCount"),2);
            Check(Resolve(2,new uint[]{3,5})==0,"component path refuses duplicate IDs within one scope");
            Marshal.WriteIntPtr(inner,uldOffset+Offset(uld,"NodeList"),innerChildren);
            Marshal.WriteInt16(inner,uldOffset+Offset(uld,"NodeListCount"),1);
            Marshal.WriteIntPtr(innerChildren,compNode);
            Check(Resolve(2,new uint[]{3,5,3})==0,"component path refuses cyclic native ownership traversal");
            Marshal.WriteIntPtr(innerChildren,node);
            var pathMethod=probe.GetMethod("ReadComponentPath",BindingFlags.NonPublic|BindingFlags.Static)!;
            foreach(var input in new[]{"{\"componentPath\":[-1]}","{\"componentPath\":[\"3\"]}","{\"componentPath\":[4294967296]}","{\"componentPath\":null}","{\"componentPath\":[],\"componentId\":3}"}){
                var refused=false;try{pathMethod.Invoke(null,new[]{parse.Invoke(null,new[]{input})});}catch(TargetInvocationException e){refused=e.InnerException?.GetType().Name=="ToolException";}
                Check(refused,"malformed/conflicting component path refused: "+input);
            }
            var tooDeep="{\"componentPath\":["+string.Join(',',Enumerable.Repeat("3",41))+"]}";
            var pathRefused=false;try{pathMethod.Invoke(null,new[]{parse.Invoke(null,new[]{tooDeep})});}catch(TargetInvocationException e){pathRefused=e.InnerException?.GetType().Name=="ToolException";}
            Check(pathRefused,"component path length is bounded before native traversal");
            var questManagerType=fcs.GetType("FFXIVClientStructs.FFXIV.Client.Game.QuestManager")!;
            var managerMemory=Allocate(Size(questManagerType));
            var repType=fcs.GetType("FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions.BeastReputationWork")!;
            var repOffset=Offset(questManagerType,"_beastReputation");
            Marshal.WriteByte(managerMemory,repOffset+Offset(repType,"Rank"),4);
            Marshal.WriteInt16(managerMemory,repOffset+Offset(repType,"Value"),123);
            var managerBox=Marshal.PtrToStructure(managerMemory,questManagerType)!;
            var sdkRep=questManagerType.GetMethod("GetBeastReputationById")!;
            var reputationPtr=(nint)Pointer.Unbox(sdkRep.Invoke(managerBox,new object[]{1U})!);
            Check(Marshal.ReadByte(reputationPtr,Offset(repType,"Rank"))==4&&Marshal.ReadInt16(reputationPtr,Offset(repType,"Value"))==123,"installed SDK reputation accessor resolves the correct synthetic work record");
            var allowanceMethod=plugin.GetType("DalamudMCP.Tools.TribalQuestTools")!.GetMethod("ReadAllowance",BindingFlags.NonPublic|BindingFlags.Static)!;
            object?[] allowanceArgs={Pointer.Box((void*)managerMemory,allowanceMethod.GetParameters()[0].ParameterType),null};
            var allowance=allowanceMethod.Invoke(null,allowanceArgs);
            Check(allowance==null&&((string?)allowanceArgs[1])?.Contains("GetBeastTribeAllowance")==true,"unresolved SDK allowance function returns unknown with its reason, never a fabricated quota");
            GC.KeepAlive(managerBox);
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
