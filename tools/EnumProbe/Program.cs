using System;
using System.Reflection;
class P {
  static void Main() {
    var asm = Assembly.LoadFrom(@"D:\AI\AI专用工作间\_refs\game\0Harmony.dll");
    var t = asm.GetType("HarmonyLib.Priority");
    foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.Static)) {
      Console.WriteLine($"  {f.Name} = {f.GetValue(null)}");
    }
  }
}
