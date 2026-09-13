using Mono.Cecil;

// FindRef <assembly> <needle>
// 扫描程序集里所有方法体，打印 IL 中引用了 <needle>（成员名/类型名子串）的 类型::方法。
// 用途：定位"谁 patch 了/调用了某个方法"（多 mod 环境下排查冲突）。
var asmPath = args[0];
var needle = args[1];

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(asmPath))!);
var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = resolver });

static IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
{
    yield return t;
    foreach (var n in t.NestedTypes.SelectMany(Flatten)) yield return n;
}

foreach (var t in asm.MainModule.Types.SelectMany(Flatten))
{
    foreach (var m in t.Methods)
    {
        if (!m.HasBody) continue;

        foreach (var il in m.Body.Instructions)
        {
            var op = il.Operand?.ToString();
            if (op == null || !op.Contains(needle, StringComparison.Ordinal)) continue;

            Console.WriteLine($"{t.FullName}::{m.Name}   [{il.OpCode} {op}]");
            break;
        }
    }
}
