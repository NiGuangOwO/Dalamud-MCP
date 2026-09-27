// Static loadability checks for the Dalamud plugin.
//
// These target the failure modes that are invisible at compile time and only
// show up in-game as "plugin failed to load" with no useful detail:
//
//   1. A malformed or incomplete manifest, or a DalamudApiLevel that does not
//      match the installed Dalamud - Dalamud refuses the plugin silently.
//   2. A Plugin constructor parameter whose type is not a Dalamud service type
//      (a typo or a wrong namespace compiles fine and then fails at load).
//   3. A Plugin constructor parameter Dalamud's DI container would refuse to
//      resolve. That rule is decided purely by reflection - the set of
//      registered singleton types plus [ScopedService] attributes - so the REAL
//      ServiceContainer is rebuilt offline and asked for a verdict.
//   4. A duplicate tool name. ToolRegistry.Add throws on a repeat, and tool
//      registration happens inside the Plugin constructor.
//
// What this CANNOT prove: that the service instances themselves construct in
// game, or that the game-side state they read is ready.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

internal static class Program
{
    private static int checks;
    private static int failures;
    private static string devDirStatic = "";

    private static void Check(string label, bool ok, string detail = "")
    {
        checks++;
        if (ok)
        {
            Console.WriteLine($"  [PASS] {label}");
        }
        else
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}{(detail.Length > 0 ? " -> " + detail : "")}");
        }
    }

    private static int Main()
    {
        var repo = @"C:\Github\Dalamud-MCP";
        var devDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN", "addon", "Hooks", "dev");
        devDirStatic = devDir;

        var pluginDll = Path.Combine(repo, @"src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll");
        var builtManifest = Path.Combine(repo, @"src\DalamudMCP\bin\x64\Debug\DalamudMCP.json");
        var sourceManifest = Path.Combine(repo, @"src\DalamudMCP\DalamudMCP.json");

        Console.WriteLine($"dev dir : {devDir}");
        Console.WriteLine($"plugin  : {pluginDll}");
        Console.WriteLine();

        if (!Directory.Exists(devDir)) { Console.Error.WriteLine($"dev dir not found: {devDir}"); return 2; }
        if (!File.Exists(pluginDll)) { Console.Error.WriteLine("build the plugin first"); return 2; }

        // The plugin references Dalamud with Private=false, so its dependencies are
        // not copied next to it. Resolve them from the dev directory.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var simple = new AssemblyName(args.Name).Name;
            if (simple is null) return null;
            var candidate = Path.Combine(devDir, simple + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        var dalamud = Assembly.LoadFrom(Path.Combine(devDir, "Dalamud.dll"));

        // ------------------------------------------------------------ manifest
        Console.WriteLine("manifest");
        Check("built manifest exists next to the DLL", File.Exists(builtManifest), builtManifest);
        Check("source manifest exists", File.Exists(sourceManifest));

        if (File.Exists(builtManifest))
        {
            var builtText = File.ReadAllText(builtManifest);
            var sourceText = File.Exists(sourceManifest) ? File.ReadAllText(sourceManifest) : "";
            Check("built manifest matches the source manifest", builtText == sourceText);

            using var doc = JsonDocument.Parse(builtText);
            var root = doc.RootElement;

            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;

            var internalName = Str("InternalName");
            var name = Str("Name");
            var apiLevelText = root.TryGetProperty("DalamudApiLevel", out var lvl) ? lvl.ToString() : null;
            var assemblyVersion = Str("AssemblyVersion");
            var applicableVersion = Str("ApplicableVersion");

            Check("InternalName is set", !string.IsNullOrWhiteSpace(internalName), $"'{internalName}'");
            Check("Name is set", !string.IsNullOrWhiteSpace(name), $"'{name}'");
            Check("AssemblyVersion parses as a version",
                Version.TryParse(assemblyVersion, out _), $"'{assemblyVersion}'");
            Check("ApplicableVersion is set", !string.IsNullOrWhiteSpace(applicableVersion), $"'{applicableVersion}'");

            // Dalamud compares the manifest's DalamudApiLevel against its own build
            // level and refuses the plugin on any mismatch - silently, with only a
            // validator complaint in the log. Read the installed level from Dalamud
            // itself rather than trusting a hardcoded number, so this check stays
            // honest after a Dalamud update.
            var installedLevel = ReadInstalledApiLevel(dalamud);

            if (installedLevel is int level)
            {
                var declared = int.TryParse(apiLevelText, out var d) ? d : -1;
                Check($"DalamudApiLevel ({declared}) matches the installed Dalamud ({level})",
                    declared == level, $"manifest says '{apiLevelText}', installed is {level}");
            }
            else
            {
                // Not a failure: the member this reads is internal and could move in
                // a future Dalamud, in which case the check degrades to a notice
                // rather than a false alarm.
                Console.WriteLine($"      [skip] could not read the installed API level; manifest says '{apiLevelText}'");
            }

            // The DLL filename must match InternalName for Dalamud to associate the
            // two; a mismatch is a silent load failure.
            var dllStem = Path.GetFileNameWithoutExtension(pluginDll);
            Check($"DLL name '{dllStem}' matches InternalName '{internalName}'",
                string.Equals(dllStem, internalName, StringComparison.Ordinal));
        }

        // -------------------------------------------------------------- the type
        Console.WriteLine();
        Console.WriteLine("plugin type");

        Assembly pluginAsm;
        try { pluginAsm = Assembly.LoadFrom(pluginDll); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [FAIL] could not load the plugin DLL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var plugin = pluginAsm.GetType("DalamudMCP.Plugin");
        Check("found DalamudMCP.Plugin", plugin is not null);

        var iface = dalamud.GetType("Dalamud.Plugin.IDalamudPlugin");
        Check("found Dalamud.Plugin.IDalamudPlugin", iface is not null);
        Check("Plugin implements IDalamudPlugin",
            plugin is not null && iface is not null && iface.IsAssignableFrom(plugin));
        Check("Plugin is public and not abstract",
            plugin is { IsPublic: true, IsAbstract: false });
        Check("Plugin is disposable",
            plugin is not null && typeof(IDisposable).IsAssignableFrom(plugin));

        var ctors = plugin!.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Check("Plugin has exactly one public constructor", ctors.Length == 1, $"found {ctors.Length}");

        if (ctors.Length == 1)
        {
            var parameters = ctors[0].GetParameters();
            Console.WriteLine($"      constructor takes {parameters.Length} parameters");

            var inDalamud = new HashSet<string>(StringComparer.Ordinal);
            var inPlugin = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in SafeTypes(dalamud)) if (t.FullName is not null) inDalamud.Add(t.FullName);
            foreach (var t in SafeTypes(pluginAsm)) if (t.FullName is not null) inPlugin.Add(t.FullName);

            var unknown = new List<string>();
            foreach (var p in parameters)
            {
                var full = p.ParameterType.FullName ?? p.ParameterType.Name;

                // A service parameter must be a type that actually lives in Dalamud
                // (or one of the assemblies it ships). Being in the plugin's own
                // assembly would mean a self-defined type, which Dalamud cannot inject.
                var known = inDalamud.Contains(full) || !inPlugin.Contains(full);
                if (!known)
                {
                    unknown.Add($"{p.Name}: {full} (defined in the plugin, not in Dalamud)");
                }

                var ns = p.ParameterType.Namespace ?? "";
                var looksLikeService = ns.StartsWith("Dalamud.", StringComparison.Ordinal);
                if (!looksLikeService)
                {
                    unknown.Add($"{p.Name}: {full} (namespace '{ns}' is not a Dalamud namespace)");
                }
            }

            Check("every constructor parameter is a Dalamud service type",
                unknown.Count == 0, string.Join(" | ", unknown));
        }

        // ------------------------------------------------------------ container
        // The check above only proves a parameter's type EXISTS. Whether Dalamud
        // will hand the plugin an instance is decided by ServiceContainer, and
        // that rule is pure reflection over registered singleton types plus
        // [ScopedService] attributes - no game state is consulted. So the real
        // container is rebuilt here and asked for its own verdict.
        Console.WriteLine();
        Console.WriteLine("container");

        if (plugin is not null && ctors.Length == 1)
        {
            var verdict = QueryDalamudContainer(dalamud, plugin, ctors[0]);

            Console.WriteLine($"      {verdict.Detail}");

            Check("Dalamud's own container accepts the plugin constructor",
                verdict.Accepted, verdict.Detail);

            Check("the container probe is capable of rejecting a constructor",
                verdict.ControlRejected, verdict.ControlDetail);

            Check("the container probe still accepts a resolvable constructor",
                verdict.ControlAccepted, verdict.ControlAcceptedDetail);

            Check("the verdict depends on the services Dalamud registers",
                verdict.ControlStarved, verdict.ControlStarvedDetail);

            Check("every parameter is reachable through Dalamud's own resolution paths",
                verdict.TotalParameters > 0 && verdict.ReachableParameters == verdict.TotalParameters,
                $"{verdict.ReachableParameters} of {verdict.TotalParameters} parameters reachable"
                + $" ({verdict.MappedParameters} interface, {verdict.DirectParameters} singleton, {verdict.ScopedParameters} scoped)");
        }

        // ---------------------------------------------------------------- tools
        Console.WriteLine();
        Console.WriteLine("tools");

        var toolSets = new[]
        {
            "DalamudMCP.Tools.ClientTools",
            "DalamudMCP.Tools.ObjectTools",
            "DalamudMCP.Tools.DataTools",
            "DalamudMCP.Tools.MemoryTools",
            "DalamudMCP.Tools.StructTools",
        };

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var setName in toolSets)
        {
            var setType = pluginAsm.GetType(setName);
            Check($"found {setName}", setType is not null);

            var register = setType?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
            Check($"{setName}.Register exists", register is not null);

            foreach (var toolName in ExtractStringLiterals(setType))
            {
                if (seen.TryGetValue(toolName, out var owner))
                    duplicates.Add($"'{toolName}' in {setName} and {owner}");
                else
                    seen[toolName] = setName;
            }
        }

        Console.WriteLine($"      distinct tool names discovered: {seen.Count}");
        Check("no duplicate tool names", duplicates.Count == 0, string.Join(" | ", duplicates));

        // Registration happens in the Plugin constructor, so a collision would stop
        // the plugin from loading at all rather than just hiding one tool.
        Check("tool count is in the expected range (25-60)", seen.Count is >= 25 and <= 60,
            $"found {seen.Count}");

        // ------------------------------------------------- struct offsets
        // StructTools reads game memory through hand-copied field offsets. If
        // FFXIVClientStructs moves a field, the tools would silently read garbage
        // (or another member's bytes) - no exception, no symptom in game. So the
        // declared constants are re-derived here from the REAL library assembly,
        // making a library update a loud test failure instead of a silent wrong
        // read. Attribute offsets are used rather than Marshal.OffsetOf because
        // the library types carry pointer members Marshal cannot lay out.
        Console.WriteLine();
        Console.WriteLine("struct offsets");
        AuditStructOffsets(dalamud, pluginAsm);

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reads the offset constants StructTools declares and compares each one
    /// against the corresponding field in the installed FFXIVClientStructs.
    /// </summary>
    private static void AuditStructOffsets(Assembly dalamud, Assembly pluginAsm)
    {
        Assembly? fcs = null;
        try { fcs = Assembly.LoadFrom(Path.Combine(devDirStatic, "FFXIVClientStructs.dll")); }
        catch (Exception ex) { Console.WriteLine($"  [warn] could not load FFXIVClientStructs: {ex.Message}"); }

        if (fcs is null)
        {
            Check("FFXIVClientStructs assembly is resolvable", false, "could not load from the dev directory");
            return;
        }

        var structTools = pluginAsm.GetType("DalamudMCP.Tools.StructTools");
        if (structTools is null)
        {
            Check("found DalamudMCP.Tools.StructTools", false);
            return;
        }

        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // The gauge-type name table: every concrete gauge struct the library ships
        // must appear exactly once, and nothing may appear that the library does
        // not have. JobGauge (the base) and ids 26/29/36 (Arcanist/Rogue/Blue Mage,
        // no dedicated gauge) are the only legitimate absences.
        var gaugeTableField = structTools.GetField("GaugeTypes", AnyStatic);
        var gaugeTable = gaugeTableField?.GetValue(null) as IReadOnlyDictionary<byte, string>;
        Check("StructTools.GaugeTypes is readable", gaugeTable is not null);

        var gaugeNamespace = "FFXIVClientStructs.FFXIV.Client.Game.Gauge";
        var concreteGauges = SafeTypes(fcs)
            .Where(t => t.IsValueType && !t.IsEnum && t.IsVisible == false == false
                        && t.Namespace == gaugeNamespace
                        && t.Name.EndsWith("Gauge", StringComparison.Ordinal)
                        && t.Name != "JobGauge")
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        // IsValueType alone admits the base class JobGauge too; filter by
        // "declared gauge structs" = value types, non-enum, exact suffix.

        if (gaugeTable is not null)
        {
            var declaredNames = gaugeTable.Values.OrderBy(n => n, StringComparer.Ordinal).ToList();
            var unknown = declaredNames.Where(n => !concreteGauges.Contains(n)).ToList();
            var missing = concreteGauges.Where(n => !declaredNames.Contains(n)).ToList();

            Check("every declared gauge type exists in the library",
                unknown.Count == 0, unknown.Count > 0 ? string.Join(", ", unknown) : $"{declaredNames.Count} declared");
            Check("every concrete gauge struct is covered",
                missing.Count == 0, missing.Count > 0 ? string.Join(", ", missing) : $"{concreteGauges.Count} covered");

            // Job ids 26 Arcanist, 29 Rogue, 36 Blue Mage have no dedicated gauge
            // struct; 43 Beastmaster likewise (its gauge is served by the generic
            // one). Everything else in the ClassJob range must be declared or
            // documented as absent.
            var ids = gaugeTable.Keys.OrderBy(k => k).ToList();
            Check("gauge table keys are plausible ClassJob ids",
                ids.All(id => id is >= 1 and <= 43), string.Join(", ", ids));

            var expectedJobGauges = new byte[] { 19, 20, 21, 22, 23, 24, 25, 27, 28, 30, 31, 32, 33, 34, 35, 37, 38, 39, 40, 41, 42 };
            var keyDiff = ids.Except(expectedJobGauges).Union(expectedJobGauges.Except(ids)).ToList();
            Check("gauge table covers exactly the combat jobs known to have gauges",
                keyDiff.Count == 0, keyDiff.Count > 0 ? string.Join(", ", keyDiff) : $"{ids.Count} jobs");
        }

        // One field-offset assertion per declared constant, derived from the
        // attribute on the REAL field. A rename or relocation in the library
        // surfaces here as a named failure instead of a silent garbage read.
        CheckOffset("BattleChara.StatusManager",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara", "StatusManager"),
            Const(structTools, "BattleCharaStatusManagerOffset"), 9136);
        CheckOffset("GameObject.ObjectKind",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject", "ObjectKind"),
            Const(structTools, "GameObjectObjectKindOffset"), 144);
        CheckOffset("StatusManager.Owner",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.StatusManager", "Owner"),
            0, 0);
        CheckOffset("StatusManager._status",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.StatusManager", "_status"),
            Const(structTools, "StatusEntriesOffset"), 8);
        CheckOffset("StatusManager.SpecialStatusTimerOrDirection",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.StatusManager", "SpecialStatusTimerOrDirection"),
            Const(structTools, "StatusManagerSpecialTimerOffset"), 976);
        CheckOffset("StatusManager.NumValidStatuses",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.StatusManager", "NumValidStatuses"),
            Const(structTools, "StatusManagerNumValidStatusesOffset"), 984);
        CheckOffset("StatusManager.ExtraFlags",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.StatusManager", "ExtraFlags"),
            Const(structTools, "StatusManagerExtraFlagsOffset"), 985);
        CheckSize("StatusManager",
            declaredSize(fcs, "FFXIVClientStructs.FFXIV.Client.Game.StatusManager"),
            Const(structTools, "StatusManagerSize"), 992);
        CheckOffset("JobGaugeManager.ClassJobId",
            fieldOffset(fcs, "FFXIVClientStructs.FFXIV.Client.Game.JobGaugeManager", "ClassJobId"),
            Const(structTools, "JobGaugeManagerClassJobIdOffset"), 88);

        // Every gauge union member sits at offset 8; JobGaugeManager.StructSize 96.
        var jgm = fcs.GetType("FFXIVClientStructs.FFXIV.Client.Game.JobGaugeManager");
        var unionMembers = jgm?.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType.Namespace == gaugeNamespace && f.Name != "CurrentGauge")
            .Select(f => fieldOffsetRaw(f))
            .Distinct().ToList();
        var unionOffsets = unionMembers is null ? "?" : string.Join(", ", unionMembers);
        Check("all JobGaugeManager gauge union members share one offset",
            unionMembers is { Count: 1 }, unionMembers is null ? "JobGaugeManager not found" : $"offsets: {unionOffsets}");
        CheckOffset("JobGaugeManager union offset",
            unionMembers is { Count: 1 } ? unionMembers[0] : -1,
            Const(structTools, "JobGaugeManagerUnionOffset"), 8);
    }

    /// <summary>Attribute offset of one named field, or -1 when unavailable.</summary>
    private static int fieldOffset(Assembly fcs, string typeName, string fieldName)
    {
        var t = fcs.GetType(typeName);
        if (t is null) return -1;
        var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f is null ? -1 : fieldOffsetRaw(f);
    }

    private static int fieldOffsetRaw(FieldInfo f) =>
        f.GetCustomAttribute<System.Runtime.InteropServices.FieldOffsetAttribute>()?.Value ?? -1;

    /// <summary>The library type's own StructSize constant, or -1.</summary>
    private static int declaredSize(Assembly fcs, string typeName)
    {
        var t = fcs.GetType(typeName);
        var c = t?.GetField("StructSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        try { return c is null ? -1 : Convert.ToInt32(c.GetRawConstantValue()); }
        catch { return -1; }
    }

    /// <summary>Value of StructTools' internal constant, or -1.</summary>
    private static int Const(Type structTools, string name)
    {
        var f = structTools.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        try { return f is null ? -1 : Convert.ToInt32(f.GetRawConstantValue()); }
        catch { return -1; }
    }

    private static void CheckOffset(string label, int actual, int declared, int expected)
    {
        Check($"{label} at +{declared} (library says +{actual}, design expects +{expected})",
            actual == declared && declared == expected);
    }

    private static void CheckSize(string label, int actual, int declared, int expected)
    {
        Check($"{label} size {declared} (library says {actual}, design expects {expected})",
            actual == declared && declared == expected);
    }

    /// <summary>
    /// Asks Dalamud's own <c>ServiceContainer</c> whether it would accept the
    /// plugin constructor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container decides a constructor with pure reflection: a parameter is
    /// satisfiable when a registered singleton type is assignable to it, or when
    /// the type it maps to carries [ScopedService]. No service instance is ever
    /// dereferenced, so the container runs offline - register the interface
    /// attributes Dalamud registers, install the singleton keys Dalamud installs
    /// before any plugin loads, then call the container's own private
    /// FindApplicableCtor. The rule is Dalamud's, not a copy of it.
    /// </para>
    /// <para>
    /// The singleton keys are installed the way InitializeEarlyLoadableServices
    /// installs them: one key per concrete IServiceType that is not [ScopedService],
    /// because Service&lt;T&gt;.GetAsync is fired for exactly those and registering
    /// the instance is how they reach the container. Scoped services are absent
    /// from instances by design.
    /// </para>
    /// </remarks>
    private static ContainerVerdict QueryDalamudContainer(Assembly dalamud, Type plugin, ConstructorInfo ctor)
    {
        var containerType = dalamud.GetType("Dalamud.IoC.Internal.ServiceContainer");
        if (containerType is null) return ContainerVerdict.Unavailable("Dalamud.IoC.Internal.ServiceContainer not found");

        var serviceInterface = dalamud.GetType("Dalamud.IServiceType");
        if (serviceInterface is null) return ContainerVerdict.Unavailable("Dalamud.IServiceType not found");

        var visibilityType = dalamud.GetType("Dalamud.IoC.Internal.ObjectInstanceVisibility");
        if (visibilityType is null) return ContainerVerdict.Unavailable("ObjectInstanceVisibility not found");

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

        object container;
        try
        {
            // The constructor itself registers IServiceContainer, exactly as in game.
            container = Activator.CreateInstance(containerType, nonPublic: true)!;
        }
        catch (Exception ex)
        {
            return ContainerVerdict.Unavailable($"could not construct ServiceContainer: {ex.GetType().Name}: {ex.Message}");
        }

        // Rebuild the interface map the way InitializeEarlyLoadableServices does:
        // RegisterInterfaces runs for every concrete IServiceType, scoped included.
        var registerInterfaces = containerType.GetMethod("RegisterInterfaces", Any | BindingFlags.Instance);
        var registerSingleton = containerType.GetMethod("RegisterSingleton", Any | BindingFlags.Instance);
        var findConstructor = containerType.GetMethod("FindApplicableCtor", Any | BindingFlags.Instance);

        if (registerInterfaces is null || registerSingleton is null || findConstructor is null)
            return ContainerVerdict.Unavailable("ServiceContainer no longer exposes RegisterInterfaces/RegisterSingleton/FindApplicableCtor");

        var mappedTypes = 0;
        foreach (var candidate in SafeTypes(dalamud))
        {
            if (candidate.IsInterface || candidate.IsAbstract) continue;
            if (!candidate.IsAssignableTo(serviceInterface)) continue;

            try { registerInterfaces.Invoke(container, new object?[] { candidate }); mappedTypes++; }
            catch { /* one unmappable attribute target must not sink the probe */ }
        }

        // Which types get a singleton key is Dalamud's own decision, so ask it
        // rather than hard-coding names: InitializeEarlyLoadableServices fires
        // Service<T>.GetAsync for every concrete IServiceType whose kind is not
        // ScopedService, and that registration is what puts the type in instances.
        var serviceManager = dalamud.GetType("Dalamud.ServiceManager");
        var getServiceKind = serviceManager?.GetMethod("GetServiceKind", BindingFlags.Public | BindingFlags.Static);
        if (getServiceKind is null) return ContainerVerdict.Unavailable("Dalamud.ServiceManager.GetServiceKind not found");

        var visibility = Enum.ToObject(visibilityType, 1); // ExposedToPlugins
        var fromResult = typeof(Task).GetMethod("FromResult", BindingFlags.Public | BindingFlags.Static)!;
        var installed = 0;
        var skippedScoped = 0;

        foreach (var candidate in SafeTypes(dalamud))
        {
            if (candidate.IsInterface || candidate.IsAbstract) continue;
            if (!candidate.IsAssignableTo(serviceInterface)) continue;

            int kind;
            try { kind = Convert.ToInt32(getServiceKind.Invoke(null, new object?[] { candidate })); }
            catch { continue; }

            // ServiceKind.ScopedService = 8; scoped services are never singletons.
            if (kind == 8) { skippedScoped++; continue; }

            try
            {
                // A null instance is fine: the container only ever inspects the
                // TYPE of a registered key, never the value behind it.
                var task = fromResult.MakeGenericMethod(candidate).Invoke(null, new object?[] { null })!;
                registerSingleton.MakeGenericMethod(candidate).Invoke(container, new[] { task, visibility });
                installed++;
            }
            catch { /* a refusal here is reported through the verdict, not thrown */ }
        }

        // LocalPlugin hands the plugin its interface as a scoped object, so the
        // probe must too. The instance is never used, only its type.
        var interfaceType = dalamud.GetType("Dalamud.Plugin.DalamudPluginInterface");
        if (interfaceType is null) return ContainerVerdict.Unavailable("Dalamud.Plugin.DalamudPluginInterface not found");

        object scopedObject;
        try { scopedObject = RuntimeHelpers.GetUninitializedObject(interfaceType); }
        catch (Exception ex) { return ContainerVerdict.Unavailable($"could not stand in for the plugin interface: {ex.GetType().Name}"); }

        var scopedObjects = new[] { scopedObject };

        var instanceKeys = (IDictionary)(containerType.GetProperty("Instances", Any | BindingFlags.Instance)?.GetValue(container) ?? new Dictionary<Type, object>());
        var interfaceMap = (IDictionary)(containerType.GetProperty("InterfaceToTypeMap", Any | BindingFlags.Instance)?.GetValue(container) ?? new Dictionary<Type, Type>());

        var parameters = ctor.GetParameters();
        var mapped = 0;
        var direct = 0;
        var scoped = 0;
        var unreachable = new List<string>();

        foreach (var parameter in parameters)
        {
            if (interfaceMap.Contains(parameter.ParameterType)) { mapped++; continue; }
            if (scopedObjects.Any(o => o.GetType().IsAssignableTo(parameter.ParameterType))) { scoped++; continue; }
            if (instanceKeys.Keys.Cast<Type>().Any(k => k.IsAssignableTo(parameter.ParameterType))) { direct++; continue; }

            unreachable.Add($"{parameter.Name}: {parameter.ParameterType.FullName}");
        }

        var verdict = new ContainerVerdict
        {
            TotalParameters = parameters.Length,
            MappedParameters = mapped,
            DirectParameters = direct,
            ScopedParameters = scoped,
            Detail = $"{mappedTypes} service types mapped, {installed} singletons installed, {skippedScoped} scoped left out"
                     + (unreachable.Count > 0 ? $" | unreachable: {string.Join(", ", unreachable)}" : ""),
        };

        try
        {
            var selected = findConstructor.Invoke(container, new object?[] { plugin, scopedObjects });
            verdict.Accepted = selected is not null;
            verdict.Detail = $"{verdict.Detail} | {mapped} interface, {direct} singleton, {scoped} scoped"
                             + (selected is null ? " | the container found no satisfiable constructor" : "");
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
            verdict.Detail = $"{verdict.Detail} | the container threw: {inner.GetType().Name}: {inner.Message}";
        }

        // The controls. A probe that cannot say NO proves nothing, and a probe
        // that always says NO proves nothing either, so both directions are asked
        // on the same code path. System.Version was tried first and was useless:
        // it has a parameterless constructor, and ValidateCtor accepts an empty
        // parameter list without consulting a single service.
        try
        {
            // A refusal only means the probe works if the constructor it refused
            // actually existed. FindApplicableCtor also returns null when a type
            // offers no usable constructor, so that case is excluded explicitly.
            var controlCtors = typeof(UnsatisfiableControl).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            var rejected = findConstructor.Invoke(container, new object?[] { typeof(UnsatisfiableControl), scopedObjects });

            verdict.ControlRejected = controlCtors.Length == 1 && rejected is null;
            verdict.ControlDetail = controlCtors.Length != 1
                ? $"the negative control exposes {controlCtors.Length} public constructors, so a refusal cannot be attributed to unsatisfiable parameters"
                : rejected is null
                    ? $"the one public constructor needing {typeof(string).FullName} was refused"
                    : "the probe ACCEPTED a constructor needing a type no service can supply - the probe is not measuring anything";
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
            verdict.ControlDetail = $"the control threw: {inner.GetType().Name}: {inner.Message}";
        }

        try
        {
            var accepted = findConstructor.Invoke(container, new object?[] { typeof(SatisfiableControl), scopedObjects });
            verdict.ControlAccepted = accepted is not null;
            verdict.ControlAcceptedDetail = accepted is not null
                ? "a parameterless constructor was accepted, so the probe is not simply refusing everything"
                : "the probe REFUSED a parameterless constructor - it is refusing everything, which would make the verdict above meaningless";
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
            verdict.ControlAcceptedDetail = $"the positive control threw: {inner.GetType().Name}: {inner.Message}";
        }

        // The causal control. The verdict above rests on the claim that installing
        // one singleton key per non-scoped service is what makes the constructor
        // resolve. If a container holding the SAME interface map but none of those
        // keys also accepted it, the keys would be decoration and the verdict would
        // be an artifact of the rebuild rather than a statement about Dalamud.
        try
        {
            var starved = Activator.CreateInstance(containerType, nonPublic: true)!;
            foreach (var candidate in SafeTypes(dalamud))
            {
                if (candidate.IsInterface || candidate.IsAbstract) continue;
                if (!candidate.IsAssignableTo(serviceInterface)) continue;

                try { registerInterfaces.Invoke(starved, new object?[] { candidate }); }
                catch { }
            }

            var starvedPick = findConstructor.Invoke(starved, new object?[] { plugin, scopedObjects });

            verdict.ControlStarved = starvedPick is null;
            verdict.ControlStarvedDetail = starvedPick is null
                ? "the same constructor is refused when the singleton keys are withheld, so those keys are what the verdict rests on"
                : "the constructor was ACCEPTED with no singletons installed - the verdict does not depend on the registered services, so it proves nothing about Dalamud";
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
            verdict.ControlStarvedDetail = $"the starvation control threw: {inner.GetType().Name}: {inner.Message}";
        }

        return verdict;
    }

    /// <summary>The container's answer about one constructor.</summary>
    private sealed class ContainerVerdict
    {
        public bool Accepted;

        public string Detail = "";

        public int TotalParameters;

        public int MappedParameters;

        public int DirectParameters;

        public int ScopedParameters;

        public bool ControlRejected;

        public string ControlDetail = "";

        public bool ControlAccepted;

        public string ControlAcceptedDetail = "";

        public bool ControlStarved;

        public string ControlStarvedDetail = "";

        public int ReachableParameters => MappedParameters + DirectParameters + ScopedParameters;

        public static ContainerVerdict Unavailable(string reason) => new() { Detail = reason };
    }

    /// <summary>
    /// Negative control: one public constructor whose single parameter is a type
    /// no Dalamud service can ever be assigned to, and no parameterless
    /// constructor for ValidateCtor to accept trivially.
    /// </summary>
    private sealed class UnsatisfiableControl
    {
        public UnsatisfiableControl(string neverAService)
        {
            _ = neverAService;
        }
    }

    /// <summary>
    /// Positive control: a parameterless constructor must still be accepted, so
    /// a probe that simply refuses everything cannot look like a pass.
    /// </summary>
    private sealed class SatisfiableControl
    {
    }

    /// <summary>
    /// The API level the installed Dalamud was built for. A manifest declaring a
    /// different number is refused at load time, so a mismatch has to be caught
    /// here rather than by the game.
    /// </summary>
    /// <remarks>
    /// This lives on an internal type, which is why it is read by name and the
    /// caller treats a miss as "unknown" instead of a failure.
    /// </remarks>
    private static int? ReadInstalledApiLevel(Assembly dalamud)
    {
        var manager = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
        var property = manager?.GetProperty("DalamudApiLevel",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        try { return property?.GetValue(null) as int?; }
        catch { return null; }
    }

    /// <summary>
    /// The lowercase_with_underscores user-string literals in a tool set. Tool
    /// names are the only things in these classes shaped that way, so the filter
    /// is safe; a literal with a capital letter or a space is a message, not a name.
    /// </summary>
    private static IEnumerable<string> ExtractStringLiterals(Type? setType)
    {
        if (setType?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static) is not { } method)
        {
            yield break;
        }

        var body = method.GetMethodBody();
        var blob = body?.GetILAsByteArray();
        if (blob is null) yield break;

        var module = method.Module;
        // ldstr is 0x72 followed by a 4-byte metadata token.
        for (var i = 0; i + 4 < blob.Length; i++)
        {
            if (blob[i] != 0x72) continue;

            string? value;
            try { value = module.ResolveString(BitConverter.ToInt32(blob, i + 1)); }
            catch { continue; }

            if (value is null || value.Length is 0 or > 40) continue;
            if (!value.Contains('_')) continue;

            var isToolName = true;
            foreach (var c in value)
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') continue;
                isToolName = false;
                break;
            }

            if (isToolName) yield return value;
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
