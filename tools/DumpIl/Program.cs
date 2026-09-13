using Mono.Cecil;
using System.Text;

// DumpIl <assembly> <TypeFullName> <MethodName|*>
//   MethodName = "*" → 输出整个类型：字段/属性/方法清单（含 virtual/sealed/静态）＋ 所有方法的 IL。
// 用途：确认游戏内部真实逻辑（不靠猜），以及"自己复刻一个类"时所需的完整签名与字段语义。
var asmPath = args[0];
var typeName = args[1];
var methodName = args[2];

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(asmPath))!);
var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = resolver });

var type = asm.MainModule.GetType(typeName);
if (type == null) { Console.Error.WriteLine($"type not found: {typeName}"); return 1; }

// 类型自身带泛型参数时 GetType 找不到，退化为全模块搜索
if (type == null)
{
    type = asm.MainModule.Types.SelectMany(Flatten).FirstOrDefault(t => t.FullName == typeName);
}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes.SelectMany(Flatten)) yield return n;
}

string Flags(MethodDefinition m)
{
    var s = new StringBuilder();
    if (m.IsStatic) s.Append("static ");
    if (m.IsPublic) s.Append("public ");
    else if (m.IsFamily) s.Append("protected ");
    else if (m.IsAssembly) s.Append("internal ");
    else if (m.IsFamilyOrAssembly) s.Append("protected internal ");
    else s.Append("private ");
    if (m.IsVirtual) s.Append(m.IsNewSlot ? "virtual " : "override ");
    if (m.IsFinal) s.Append("sealed ");
    if (m.IsAbstract) s.Append("abstract ");
    return s.ToString().TrimEnd();
}

Console.WriteLine($"===== TYPE {type.FullName} : base={type.BaseType?.FullName} [{string.Join(",", type.Interfaces.Select(i => i.InterfaceType.Name))}] =====");

if (methodName == "*")
{
    Console.WriteLine("--- 字段 ---");
    foreach (var f in type.Fields)
        Console.WriteLine($"  {(f.IsStatic ? "static " : "")}{(f.IsPublic ? "public" : f.IsFamily ? "protected" : f.IsAssembly ? "internal" : "private")} {f.FieldType.Name} {f.Name}");

    Console.WriteLine("--- 属性 ---");
    foreach (var p in type.Properties)
    {
        var g = p.GetMethod; var s = p.SetMethod;
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name} {{ {(g != null ? "get;" : "")}{(s != null ? " set;" : "")} }}  get={(g != null ? Flags(g) : "-")} set={(s != null ? Flags(s) : "-")}");
    }

    Console.WriteLine("--- 方法 ---");
    foreach (var m in type.Methods)
        Console.WriteLine($"  {Flags(m)} {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
    Console.WriteLine();
}

Console.WriteLine("--- IL ---");
foreach (var m in type.Methods.Where(m => methodName == "*" || m.Name == methodName))
{
    Console.WriteLine($"=== [{Flags(m)}] {type.Name}.{m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))}) : {m.ReturnType.Name} ===");
    if (!m.HasBody) { Console.WriteLine("  (no body)"); Console.WriteLine(); continue; }
    foreach (var v in m.Body.Variables)
        Console.WriteLine($"  .local {v.VariableType.Name} V_{v.Index}");
    foreach (var il in m.Body.Instructions)
        Console.WriteLine($"  IL_{il.Offset:X4}: {il.OpCode,-12} {il.Operand}");
    Console.WriteLine();
}
return 0;
