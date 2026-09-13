using Mono.Cecil;
using System.Text;

// Usage: DumpStrings <assemblyPath> <outputTxt> [filterRegex]
// 用 Mono.Cecil 读程序集里全部字符串字面量（ldstr + 自定义特性参数）。不加载依赖。
var asmPath = args[0];
var outPath = args[1];
var filter = args.Length > 2 ? args[2] : null;

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(asmPath))!);
foreach (var d in (args.Length > 3 ? args[3] : "").Split(';', StringSplitOptions.RemoveEmptyEntries))
    resolver.AddSearchDirectory(d);

var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver });
var set = new SortedSet<string>(StringComparer.Ordinal);

foreach (var mod in asm.Modules)
{
    foreach (var t in mod.GetTypes())
    {
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var il in m.Body.Instructions)
            {
                if (il.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && il.Operand is string s) set.Add(s);
            }
        }
        foreach (var f in t.Fields)
        {
            if (f.HasConstant && f.Constant is string cs) set.Add(cs);
        }
        foreach (var p in t.Properties)
        {
            if (p.HasConstant && p.Constant is string ps) set.Add(ps);
        }
        foreach (var ca in t.CustomAttributes) Collect(ca, set);
        foreach (var m in t.Methods)
            foreach (var ca in m.CustomAttributes) Collect(ca, set);
    }
}

static void Collect(CustomAttribute ca, SortedSet<string> set)
{
    bool has;
    try { has = ca.HasConstructorArguments; } catch { return; }
    if (!has) return;
    foreach (var a in ca.ConstructorArguments)
    {
        if (a.Value is string s) set.Add(s);
        else if (a.Value is IEnumerable<CustomAttributeArgument> arr)
            foreach (var x in arr) if (x.Value is string s2) set.Add(s2);
    }
}

File.WriteAllLines(outPath, set, new UTF8Encoding(false));
Console.WriteLine($"distinctStrings={set.Count} written={outPath}");

if (filter != null)
{
    var rx = new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var hits = set.Where(s => rx.IsMatch(s)).ToList();
    Console.WriteLine($"--- /{filter}/ -> {hits.Count} hits ---");
    foreach (var h in hits.Take(80)) Console.WriteLine("  " + h);
}
