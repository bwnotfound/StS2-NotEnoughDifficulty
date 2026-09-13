using System.Reflection;
using System.Text;

// Usage: DumpApi <assemblyPath> <outputTxt>
// Dumps every type + member of an assembly to a text file, so names can be grepped locally.

var asmPath = args[0];
var outPath = args[1];
var probeDir = Path.GetDirectoryName(asmPath)!;
// 额外探测目录（逗号分隔）：用于解析 GodotSharp 等不由本目录提供的依赖
var extraDirs = args.Length > 2
    ? args[2].Split(';', StringSplitOptions.RemoveEmptyEntries)
    : Array.Empty<string>();

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var simple = new AssemblyName(e.Name).Name;
    var dirs = new List<string> { probeDir };
    dirs.AddRange(extraDirs);
    foreach (var d in dirs)
    {
        foreach (var ext in new[] { ".dll", ".exe" })
        {
            var candidate = Path.Combine(d, simple + ext);
            if (File.Exists(candidate))
            {
                try { return Assembly.LoadFrom(candidate); } catch { }
            }
        }
    }
    return null;
};

Assembly asm;
try
{
    asm = Assembly.LoadFrom(asmPath);
}
catch (ReflectionTypeLoadException ex)
{
    asm = ex.Types.Where(t => t != null).Select(t => t!.Assembly).First();
    Console.Error.WriteLine("ReflectionTypeLoadException: " + ex.Message);
}

Type[] types;
try
{
    types = asm.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
    Console.WriteLine($"partial load: {types.Length} types");
}

var sb = new StringBuilder();
sb.AppendLine($"# assembly={asm.GetName().Name} version={asm.GetName().Version}");
foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
    sb.AppendLine($"TYPE\t{kind}\t{t.FullName}\tbase={t.BaseType?.FullName}\tvis={Vis(t)}");
    try
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var m in t.GetMembers(F))
        {
            switch (m)
            {
                case MethodInfo mi:
                    var ps = string.Join(", ", mi.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"\tMETHOD\t{mi.Name}({ps})\tret={mi.ReturnType.Name}\tstatic={mi.IsStatic}\tvis={Vis(mi)}");
                    break;
                case PropertyInfo pi:
                    var acc = pi.GetMethod ?? pi.SetMethod;
                    sb.AppendLine($"\tPROP\t{pi.PropertyType.Name} {pi.Name}\tstatic={(acc?.IsStatic ?? false)}\tvis={Vis(acc)}");
                    break;
                case FieldInfo fi:
                    sb.AppendLine($"\tFIELD\t{fi.FieldType.Name} {fi.Name}\tstatic={fi.IsStatic}\tvis={Vis(fi)}");
                    break;
                case ConstructorInfo ci:
                    var cps = string.Join(", ", ci.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"\tCTOR\t({cps})\tvis={Vis(ci)}");
                    break;
                case EventInfo ei:
                    sb.AppendLine($"\tEVENT\t{ei.EventHandlerType?.Name} {ei.Name}\tvis={Vis(ei.AddMethod)}");
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        sb.AppendLine($"\tERROR\t{ex.GetType().Name}: {ex.Message}");
    }
}

// 访问修饰符。**必须导出**：元数据里能读到，但看不到就很容易把 protected/private
// 成员当公开成员用 —— 我在 ActMap.Grid（protected）和 NMapScreen.RecalculateTravelability
// （protected/private）上各栽过一次，靠编译器才拦住。
// 注意：C# 顶层语句不支持局部函数重载，所以这里用模式匹配统一成一个函数。
static string Vis(object? member) => member switch
{
    Type t => t.IsNestedPublic || t.IsPublic ? "public"
            : t.IsNestedFamily ? "protected"
            : t.IsNestedFamORAssem ? "protected internal"
            : t.IsNestedAssembly ? "internal"
            : "private",

    MethodBase m => m.IsPublic ? "public"
                  : m.IsFamily ? "protected"
                  : m.IsFamilyOrAssembly ? "protected internal"
                  : m.IsAssembly ? "internal"
                  : m.IsFamilyAndAssembly ? "private protected"
                  : "private",

    FieldInfo f => f.IsPublic ? "public"
                 : f.IsFamily ? "protected"
                 : f.IsFamilyOrAssembly ? "protected internal"
                 : f.IsAssembly ? "internal"
                 : f.IsFamilyAndAssembly ? "private protected"
                 : "private",

    _ => "?"
};

File.WriteAllText(outPath, sb.ToString());
var errLines = sb.ToString().Split('\n').Count(l => l.Contains("\tERROR\t"));
Console.WriteLine($"types={types.Length} memberEnumerationErrors={errLines} written={outPath} bytes={new FileInfo(outPath).Length}");
