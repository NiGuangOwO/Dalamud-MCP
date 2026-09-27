using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace PluginLoadTest;

/// <summary>
/// Builds a REAL IDataManager at runtime, backed by the locally installed game's sqpack files.
///
/// Why this exists, in two parts.
///
/// 1. Lumina reads sqpack files directly, with no game process running. That was proven with a
///    standalone probe: constructing Lumina.GameData against
///    "C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\sqpack" enumerated 7912 sheets and
///    resolved Item row 1 to 金币.
///
/// 2. The obvious way to hand that module to the plugin - a DispatchProxy - CANNOT work, and
///    that is not a guess. DispatchProxy does not copy a method's generic parameter constraints
///    into its generated override, so an override of
///        ExcelSheet&lt;T&gt; GetExcelSheet&lt;T&gt;() where T : struct, IExcelRow&lt;T&gt;
///    dies in the generated method body with
///        TypeLoadException: GenericArguments[0], 'T', on 'Lumina.Excel.ExcelSheet`1[T]'
///        violates the constraint of type parameter 'T'
///    before the handler is ever reached. A seven-control probe isolated the rule exactly:
///    F-bounded constraint alone is fine (returned as itself, or used as an argument), a
///    constrained generic return class is fine under a plain struct constraint, and only the
///    COMBINATION - an F-bounded type parameter flowing into a class constrained the same way -
///    fails. The real signature is that combination, so the inert proxy in this test reported a
///    TypeLoadException for six data tools regardless of plugin correctness, which made the test
///    lie about why they failed.
///
/// Reflection.Emit does copy the constraints, so a generated type relays the call correctly and
/// the plugin's data tools run against real game data instead of an inert stub.
/// </summary>
internal static class RealDataManager
{
    internal sealed class Result
    {
        public object? Instance { get; init; }
        public bool IsReal => Instance is not null;
        public string Detail { get; init; } = string.Empty;

        /// <summary>Set only when the emitted manager was exercised: the resolved item 1 name.</summary>
        public string ProbeItemName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Locates the game's sqpack directory. Order: explicit override, then the paths that exist
    /// on this machine, then the path Dalamud itself last logged as ready - which is the most
    /// reliable signal because it is what the launcher actually resolved, and it survives the
    /// game being installed somewhere unexpected.
    /// </summary>
    internal static string? FindSqpack()
    {
        var overridePath = Environment.GetEnvironmentVariable("DALAMUD_MCP_SQPACK");
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
            return overridePath;

        var candidates = new[]
        {
            @"C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\sqpack",
            @"C:\FF14Wegame\wg_ff14\game\sqpack",
            @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"C:\Program Files\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
        };
        foreach (var candidate in candidates)
            if (Directory.Exists(Path.Combine(candidate, "ffxiv")))
                return candidate;

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var launcher in new[] { "XIVLauncherCN", "XIVLauncher" })
            {
                var logPath = Path.Combine(appData, launcher, "dalamud.log");
                if (!File.Exists(logPath)) continue;

                // Read the tail; the log is large and the interesting line is the recent one.
                var lines = File.ReadLines(logPath).Reverse().Take(4000);
                foreach (var line in lines)
                {
                    const string marker = "Lumina is ready: ";
                    var at = line.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;

                    var path = line[(at + marker.Length)..].Trim();
                    if (Directory.Exists(path)) return path;
                }
            }
        }
        catch (Exception)
        {
            // A missing or unreadable log is not an error; the literal candidates above may
            // still have matched, and the caller reports unavailability honestly either way.
        }

        return null;
    }

    /// <summary>
    /// Emits the manager and, if a sqpack directory is available, constructs it against real
    /// game data and proves the F-bounded generic relays by reading a row.
    /// </summary>
    internal static Result TryCreate(Assembly dalamud, string? sqpackPath)
    {
        Type idm;
        Type excelModule;
        Type gameDataType;
        try
        {
            idm = dalamud.GetType("Dalamud.Plugin.Services.IDataManager", throwOnError: true)!;

            // Lumina.Excel.ExcelModule, Lumina.Excel.ExcelSheet`1 and Lumina.Excel.IExcelRow`1 all
            // live in Lumina.dll, NOT Lumina.Excel.dll - the latter holds only the Sheets.* row
            // types. Resolving them against the wrong assembly returns null and the failure then
            // surfaces much later as a confusing DefineField/ArgumentNullException.
            var lumina = Assembly.Load("Lumina");
            excelModule = lumina.GetType("Lumina.Excel.ExcelModule", throwOnError: true)!;
            gameDataType = lumina.GetType("Lumina.GameData", throwOnError: true)!;
        }
        catch (Exception ex)
        {
            return new Result { Detail = $"Lumina/IDataManager types not resolvable: {Flatten(ex)}" };
        }

        Type? emitted;
        try
        {
            emitted = Emit(idm, excelModule, gameDataType);
        }
        catch (Exception ex)
        {
            return new Result { Detail = $"emitting the manager failed: {Flatten(ex)}" };
        }

        if (sqpackPath is null)
            return new Result { Detail = "no sqpack directory was found on this machine" };

        try
        {
            var optionsType = Assembly.Load("Lumina").GetType("Lumina.LuminaOptions", throwOnError: true)!;
            var options = Activator.CreateInstance(optionsType)!;
            var language = optionsType.GetProperty("DefaultExcelLanguage")!;
            language.SetValue(options, Enum.Parse(language.PropertyType, "ChineseSimplified"));

            var gameData = Activator.CreateInstance(gameDataType, new object[] { sqpackPath, options })!;
            var excel = gameDataType.GetProperty("Excel")!.GetValue(gameData)!;
            var instance = Activator.CreateInstance(emitted!, new[] { excel, gameData })!;

            return new Result
            {
                Instance = instance,
                Detail = sqpackPath,
                ProbeItemName = ProbeItemName(idm, instance, sqpackPath),
            };
        }
        catch (Exception ex)
        {
            return new Result { Detail = $"reading {sqpackPath} failed: {Flatten(ex)}" };
        }
    }

    /// <summary>
    /// Reads Item row 1 back through the EMITTED manager. This is the real proof: it goes through
    /// the generated override of the F-bounded generic, which is exactly the call DispatchProxy
    /// could not serve.
    /// </summary>
    private static string ProbeItemName(Type idm, object instance, string sqpackPath)
    {
        try
        {
            var itemType = Assembly.Load("Lumina.Excel").GetType("Lumina.Excel.Sheets.Item", throwOnError: true)!;
            var getSheet = idm.GetMethods().First(m => m.Name == "GetExcelSheet");
            var sheet = getSheet.MakeGenericMethod(itemType).Invoke(instance, new object?[] { null, null });
            if (sheet is null) return "(sheet was null)";

            var getRow = sheet.GetType().GetMethods().First(m =>
                m.Name == "GetRow" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(uint));
            var row = getRow.Invoke(sheet, new object[] { 1u });
            if (row is null) return "(row was null)";

            return itemType.GetProperty("Name")!.GetValue(row)?.ToString() ?? "(name was null)";
        }
        catch (Exception ex)
        {
            return $"(probe failed: {Flatten(ex)})";
        }
    }

    // ------------------------------------------------------------ the emitter

    private static Type Emit(Type idm, Type excelModule, Type gameDataType)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PluginLoadTest.DynamicDataManager"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("DynamicDataManager");

        var builder = module.DefineType(
            "SqpackDataManager",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(object),
            new[] { idm });

        var excelField = builder.DefineField("_excel", excelModule, FieldAttributes.Private | FieldAttributes.InitOnly);
        var gameField = builder.DefineField("_game", gameDataType, FieldAttributes.Private | FieldAttributes.InitOnly);

        var ctor = builder.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, new[] { excelModule, gameDataType });
        var ctorIl = ctor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        ctorIl.Emit(OpCodes.Ldarg_0); ctorIl.Emit(OpCodes.Ldarg_1); ctorIl.Emit(OpCodes.Stfld, excelField);
        ctorIl.Emit(OpCodes.Ldarg_0); ctorIl.Emit(OpCodes.Ldarg_2); ctorIl.Emit(OpCodes.Stfld, gameField);
        ctorIl.Emit(OpCodes.Ret);

        // EVERY interface member must be emitted. Skipping the generic overloads that look
        // irrelevant produces "Method 'GetFile' in type 'RealDataManager' does not have an
        // implementation" at CreateType, so the ones that do not matter get default bodies.
        foreach (var method in idm.GetMethods())
        {
            var parameters = method.GetParameters();
            var methodBuilder = builder.DefineMethod(
                method.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                CallingConventions.HasThis);

            GenericTypeParameterBuilder[]? genericParameters = null;
            var declared = Type.EmptyTypes;
            var returnType = method.ReturnType;

            if (method.IsGenericMethodDefinition)
            {
                declared = method.GetGenericArguments();
                genericParameters = methodBuilder.DefineGenericParameters(declared.Select(g => g.Name).ToArray());
                for (var i = 0; i < genericParameters.Length; i++)
                {
                    // Copying the constraints is the entire point of using Reflection.Emit here:
                    // this is the step DispatchProxy omits.
                    genericParameters[i].SetGenericParameterAttributes(declared[i].GenericParameterAttributes);
                    var interfaces = declared[i].GetGenericParameterConstraints()
                        .Where(c => c.IsInterface)
                        .Select(c => Substitute(c, declared, genericParameters))
                        .ToArray();
                    if (interfaces.Length > 0) genericParameters[i].SetInterfaceConstraints(interfaces);
                }

                returnType = Substitute(method.ReturnType, declared, genericParameters);
            }

            methodBuilder.SetReturnType(returnType);
            methodBuilder.SetParameters(parameters
                .Select(p => Substitute(p.ParameterType, declared, genericParameters ?? Array.Empty<GenericTypeParameterBuilder>()))
                .ToArray());

            var il = methodBuilder.GetILGenerator();

            if (genericParameters is not null && (method.Name == "GetExcelSheet" || method.Name == "GetSubrowExcelSheet"))
            {
                var target = excelModule.GetMethods().First(m =>
                    m.Name == (method.Name == "GetExcelSheet" ? "GetSheet" : "GetSubrowSheet") &&
                    m.IsGenericMethodDefinition);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, excelField);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, target.MakeGenericMethod(genericParameters[0]));
                il.Emit(OpCodes.Ret);
            }
            else if (method.Name == "get_Excel")
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, excelField); il.Emit(OpCodes.Ret);
            }
            else if (method.Name == "get_GameData")
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, gameField); il.Emit(OpCodes.Ret);
            }
            else if (method.Name == "get_HasModifiedGameDataFiles" || method.Name == "FileExists")
            {
                il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            }
            else
            {
                EmitDefault(il, returnType);
            }

            builder.DefineMethodOverride(methodBuilder, method);
        }

        return builder.CreateType()!;
    }

    /// <summary>Rewrites <paramref name="type"/> replacing occurrences of each declared generic parameter.</summary>
    private static Type Substitute(Type type, Type[] from, GenericTypeParameterBuilder[] to)
    {
        var index = Array.IndexOf(from, type);
        if (index >= 0) return to[index];
        if (!type.IsGenericType || type.IsGenericTypeDefinition) return type;

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments().Select(a => Substitute(a, from, to)).ToArray();
        return definition.MakeGenericType(arguments);
    }

    private static void EmitDefault(ILGenerator il, Type returnType)
    {
        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Ret);
            return;
        }

        if (returnType.IsValueType)
        {
            var local = il.DeclareLocal(returnType);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Initobj, returnType);
            il.Emit(OpCodes.Ldloc, local);
        }
        else if (returnType.IsGenericParameter)
        {
            // default(T): a generic parameter is not a value type as far as the IL has to be
            // concerned, but it also cannot be null-loaded, so it needs a local either way.
            var local = il.DeclareLocal(returnType);
            il.Emit(OpCodes.Ldloc, local);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Ret);
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }
}
