using System.Security.Cryptography;
using System.Text;

// MakePck — 程序化生成 Godot 4.x PCK（packFormat 3），格式依据本机 BonModConfig.pck / BaseLib.pck 实测：
//   Header(40B): u32 magic=0x43504447 'GDPC'; u32 packFormat=3; u32 4,5,1; u32 flags=0x2;
//                u64 filesBase; u64 dirOffset
//   Dir: u32 count; per entry: u32 pathLen(含'\0'); utf8 path(+'\0'); u64 offset(相对 filesBase,16B对齐);
//                u64 size; u8[16] md5; u32 entryFlags=0
//   flags bit1 (0x2) = 偏移相对 filesBase；实测两个模组 pck 的最小偏移均为 0，故为相对语义。
//
// Usage: MakePck <out.pck> <nameInPck> <srcDir> [file1 file2 ...]
//        未给文件列表时，递归打包 srcDir 下所有文件。

const uint MAGIC = 0x43504447;
const uint PACK_FORMAT = 3;
const uint FLAG_REL_FILEBASE = 0x2;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: MakePck <out.pck> <nameInPck> <srcDir> [files...]");
    return 2;
}

var outPck = Path.GetFullPath(args[0]);
var pckName = args[1].Trim('/');
var srcDir = Path.GetFullPath(args[2]);

var files = args.Length > 3
    ? args.Skip(3).Select(f => Path.GetFullPath(f)).ToList()
    : Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList();

if (files.Count == 0) { Console.Error.WriteLine("没有待打包文件"); return 2; }

// 组装条目：pck 内路径 = <pckName>/<相对 srcDir 的路径>
var entries = new List<(string PckPath, byte[] Data)>();
foreach (var f in files)
{
    var rel = Path.GetRelativePath(srcDir, f).Replace('\\', '/');
    var data = File.ReadAllBytes(f);
    entries.Add(($"{pckName}/{rel}", data));
}
entries = entries.OrderBy(e => e.PckPath, StringComparer.Ordinal).ToList();

// 计算目录表字节数
int DirSize()
{
    var n = 4; // count
    foreach (var e in entries)
    {
        var pathLen = Encoding.UTF8.GetByteCount(e.PckPath) + 1; // +1 结尾 '\0'
        n += 4 + pathLen + 8 + 8 + 16 + 4;
    }
    return n;
}

var dirSize = DirSize();
var dataBase = (ulong)((40 + dirSize + 15) / 16 * 16);   // 数据区起点，16 字节对齐
var dirOffset = (ulong)((40 + dirSize + 15) / 16 * 16);  // 目录表放在数据之后，稍后覆盖

// 先算每个文件的数据偏移（相对 dataBase，16 字节对齐）
var relativeOffsets = new ulong[entries.Count];
ulong cursor = 0;
for (var i = 0; i < entries.Count; i++)
{
    cursor = (cursor + 15) / 16 * 16;
    relativeOffsets[i] = cursor;
    cursor += (ulong)entries[i].Data.Length;
}
var dataEnd = dataBase + cursor;
dirOffset = (dataEnd + 15) / 16 * 16;

using var fs = new FileStream(outPck, FileMode.Create, FileAccess.Write);
using var bw = new BinaryWriter(fs);

// ---- Header ----
bw.Write(MAGIC);
bw.Write(PACK_FORMAT);
bw.Write(4u); bw.Write(5u); bw.Write(1u);
bw.Write(FLAG_REL_FILEBASE);
bw.Write(dataBase);
bw.Write(dirOffset);

// ---- Data ----
fs.Position = (long)dataBase;
for (var i = 0; i < entries.Count; i++)
{
    fs.Position = (long)(dataBase + relativeOffsets[i]);
    bw.Write(entries[i].Data);
}

// ---- Directory ----
fs.Position = (long)dirOffset;
bw.Write((uint)entries.Count);
for (var i = 0; i < entries.Count; i++)
{
    var pathBytes = Encoding.UTF8.GetBytes(entries[i].PckPath);
    bw.Write((uint)(pathBytes.Length + 1));
    bw.Write(pathBytes);
    bw.Write((byte)0);
    bw.Write(relativeOffsets[i]);
    bw.Write((ulong)entries[i].Data.Length);
    bw.Write(MD5.HashData(entries[i].Data));
    bw.Write(0u);
}

bw.Flush();
Console.WriteLine($"pck  = {outPck}");
Console.WriteLine($"files= {entries.Count}  filesBase={dataBase}  dirOffset={dirOffset}  totalBytes={fs.Length}");
foreach (var (p, _) in entries) Console.WriteLine("   " + p);
return 0;
