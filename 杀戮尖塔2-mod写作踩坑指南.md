# 杀戮尖塔 2 Mod 写作踩坑指南
> **合规口径**：本文档面向 mod 开发 —— 文中所有"读程序集 / 看 IL"的操作，都是在**本机已安装的程序集**上做**只读分析**，
> 用于确认公开 API 与运行时行为；不涉及修改、破解或再分发游戏与第三方代码 / 资源。请同时遵守游戏与社区规则。


作者：DSH agent（在三轮移植 + 一轮功能重写中总结）
基线：StS2 **public beta v0.111.0** ／ BaseLib **3.4.5 → 3.4.7** ／ **MegaDot（Godot 4.5.1）**
适用：任何 StS2 C# mod（不限本模组）

> 本指南所有结论都来自**实测**（编译验证、程序集元数据、IL 反汇编、实机日志），
> 不是"看起来应该这样"。凡是我猜错过的，都标了 ⚠️ 并写出错误版本。

---

## 第 0 章　最重要的一条：**别猜，去读程序集**

这一章放在最前面，因为它能省掉你 80% 的返工。

### 0.1 元数据转储（比读 XML 文档可靠）

游戏的 `sts2.xml` 文档**严重不全**（`ActModel`、`RunManager`、`ModelDb` 这些核心类型根本没有条目），
**不能作为判据**。正确做法是写个小工具把程序集元数据全量转储：

```csharp
// DumpApi：把每个类型的成员写成文本，便于本地 grep
Assembly asm = Assembly.LoadFrom(path);
// 关键：用 AssemblyResolve 从游戏目录补依赖，否则成员枚举会「中途截断」
AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
    var candidate = Path.Combine(probeDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};
foreach (var t in asm.GetTypes())
    foreach (var m in t.GetMembers(BindingFlags.Public|NonPublic|Instance|Static|DeclaredOnly))
        /* 输出 name / 返回类型 / static / 形参（含形参名！） */;
```

**⚠️ 我踩过的坑**：第一次转储 BaseLib 时 `GodotSharp` 解析失败，
**成员枚举静默中断**（只导出了半个 `ModConfig` 类型），于是我"证实"了 `BaseLib.Config.ModConfig.Save` 不存在——
实际它存在。**所以转储工具必须报告"成员枚举错误数"，0 才算可信。**

#### 0.1.1 ⚠️ 转储**必须导出访问修饰符**（我在这上面栽了两次）

元数据里能读到 `public / protected / internal / private`，但**如果你的转储工具不输出它，
你就会把 protected/private 成员当公开 API 用 —— 而且只能等编译器报错才知道。**

本项目实际发生的两次：

| 成员 | 真实修饰符 | 我的错误 |
|---|---|---|
| `ActMap.Grid` | **protected** | 当成公开属性直接读写 → 编译失败 |
| `NMapScreen.RecalculateTravelability()` | **private** | 当成公开方法直接调 → 编译失败 |
| `NMapPoint.RefreshState()` / `IsTravelable` | **private / protected** | 同上 |

补上之后一眼可见（实测输出）：

```
METHOD  EnterMapPointInternal(...)   vis=public      ← 可以直接调
METHOD  get_Grid()                   vis=protected   ← 只能子类 override
METHOD  RecalculateTravelability()   vis=private     ← 必须反射
```

**推论**：`protected/private` 的成员要用 `HarmonyLib.AccessTools` / `Traverse` 拿，
不要写直接调用。而且这类"看不见的访问级别"是**编译器唯一能帮你拦住**的东西之一，
剩下那些（形参名、私有字段名）编译器管不了，得靠第 3.4 节的存在性校验。

> 工具源码：`_tools\DumpApi`（输出格式 `METHOD\t签名\tret=…\tstatic=…\tvis=…`）

### 0.2 IL 反汇编（搞清游戏真实行为）

想知道某个方法到底怎么写的，别猜语义，直接反汇编：

```csharp
// DumpIl：用 Mono.Cecil 打印方法的完整 IL（含 .local、操作码、操作数）
var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
foreach (var il in method.Body.Instructions)
    Console.WriteLine($"IL_{il.Offset:X4}: {il.OpCode,-12} {il.Operand}");
```

**这一招在本项目里定位了 5 个靠猜绝对猜不出的问题**：

| 问题 | IL 揭示的真相 |
|---|---|
| 双 boss 为什么跑到第 5 层 | `GenerateRooms` 里写死 `actIndex == Acts.Count - 1`（只认「最后一个 act」），追加 act4/5 后"最后一个"变成了 act5 |
| `ActModel.GetNumberOfRooms` 不能 override | 它不是 `virtual`（只有 `BaseNumberOfRooms` 是 `protected virtual`） |
| `ActMap.Grid` 读不到 | 它是 **`protected` 抽象属性**，`ActMap.get_Grid()` 没有 body，实现和 backing field 都在 `StandardActMap` 上 |
| `MapPoint.coord` 设不进去 | 它是**私有字段、没有 setter**，构造函数的 `col/row` 参数会被忽略；只能靠 `grid[col,row] = point`（C# 对应 IL 的 `MapPoint[,]::Set`） |
| `RunState.MapPointHistory` 类型诡异 | 它是 `List<List<MapPointHistoryEntry>>` —— **按 act 分组的嵌套列表**，单层遍历必错 |

### 0.3 Harmony 的形参名必须逐字匹配

Harmony 靠**参数名**注入（`__instance` / `__result` / `___fieldName` / 原方法形参名）。
形参名写错的后果是**patch 静默不生效**（不报错）。

→ 拿形参名的唯一可靠途径：**从元数据里读**（`DumpApi` 输出里带形参名），不要凭记忆。

### 0.4 一切"能编译"的结论必须来自 **clean build**

**⚠️ 我在这个坑上给出过完全错误的结论**：
探针工程走了 incremental 复用，实际**没有重新编译**某个文件，于是我宣布"源码对当前 beta 0 错 0 警、兼容"。
清空产物重建后立刻暴露真实编译错误。**删掉 `bin/`、`obj/`、`.godot/` 再构建。**

### 0.5 ⚠️⚠️ 学会拆分：新机制十有八九只是**原版效果的组合**

**本项目最值钱的一次醒悟**（狂躁机制，2026-09-17，**用户点破**）：

> 用户："**不对啊，我为什么要搞广播这种这么复杂呢？狂躁：回合结束时在手牌内自动打出。
> 像战鼓这张牌，被消耗额外获得 2 点能量，我直接狂躁里面内置一个这样的标签这样就行了呀：
> 当狂躁标签的牌被消耗时，返回到抽牌堆第一位。
> 像奇巧这个标签，也是被弃牌触发，我一样跟着内置一个奇巧的检测：
> 当狂躁标签的牌被丢弃时，返回到抽牌堆第一位。**"

当时我已经准备开写一套"回合结束广播 + 遍历所有牌 + 玩家是否打出过标记"的补丁层。
**全是多余的。**

#### 拆分四步法

**第 1 步：把机制写成"什么事件触发"的清单**（写"什么时候发生"，不是"我要做什么"）

```
狂躁：
  IF 玩家选中打出         → 进弃牌堆      ← 原版通用行为，一行都不用写
  ELIF 回合结束 / 仍在手牌 → 自动打出
  ELIF 被丢弃             → 回抽牌堆第一位
  ELIF 被消耗             → 回抽牌堆第一位
```

**第 2 步：每个事件去原版找"谁已经在处理它"** —— 原版**必然**处理过，
因为它自己就有关键词/卡牌在干同样的事。

| 我需要的触发 | 原版谁在做 | 它 override 了什么 |
|---|---|---|
| 回合结束仍在手牌 | 虚无 Ethereal ＋ 所有 `HasTurnEndInHandEffect` 的牌 | `CardModel.OnTurnEndInHand` |
| **被丢弃**时触发 | **奇巧 Sly** | `AbstractModel.AfterCardDiscarded` |
| **被消耗**时触发 | **战鼓 `DrumOfBattle`** | `AbstractModel.AfterCardExhausted` |

**第 3 步：读那张原版牌的源码，抄它的挂点**（＝ 第 0 章的"去读程序集"）

```csharp
// CardCmd.DiscardAndDraw（反编译真身）—— 奇巧就挂在 Hook.AfterCardDiscarded
if (card.IsSlyThisTurn) slyCards.Add(card);          // ← 奇巧检测
await CardPileCmd.Add(card, discardPile);
await Hook.AfterCardDiscarded(combatState, choiceContext, card);   // ← 广播点
// …循环结束后：await AutoPlay(choiceContext, item, null, AutoPlayType.SlyDiscard);

// CardCmd.Exhaust —— 战鼓就挂在 Hook.AfterCardExhausted
await CardPileCmd.Add(card, PileType.Exhaust, CardPilePosition.Bottom, null, skipVisuals);
await Hook.AfterCardExhausted(combatState, choiceContext, card, causedByEthereal);

// 战鼓原文（CardModel 子类，没有一行 Harmony）
public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)
{ if (card == this && base.CombatState != null) { /* 加 2 点能量 */ } }
```

**⇒ 关键认知：`CardModel` 本身就是 hook listener**（`AbstractModel` 是监听者基类，
`Hook.IterateCombatHookListeners` 遍历的就是这些 model）。
`public override` 那几个虚方法，**卡牌自己就能收到广播** ——
**不需要写 Harmony patch，更不需要自己造一套广播。**

**第 4 步：确认"误伤面"** —— 挂上之后必须问：**这个挂点会不会被我不要的路径触发？**

> 狂躁的答案：不会。打出的牌进弃牌堆走 `CardPileCmd.Add` + result pile，**不经过**
> `CardCmd.DiscardAndDraw`（`CardCmd.Discard` 只是它 `cardsToDraw = 0` 的转发）
> ⇒ 主动打出**不会**触发 `AfterCardDiscarded`。

#### 反例：不拆分的代价（本项目真实弯路，最后**全废**）

| 弯路 | 为什么错 |
|---|---|
| 给 `CardPileCmd.Add` 挂**全路径 postfix** | `Add` 是 async，postfix 拿到的 Task 还没跑完，`!IsCompletedSuccessfully` **恒为真** ⇒ 从未生效 |
| 自己写"回合结束广播"，遍历所有牌 | 原版 `HasTurnEndInHandEffect` 门槛已经做完了这件事 |
| `_frenzyRound` + `_dealtDamage` 标记栈 | 自造状态机维护"这一趟是不是狂躁" —— 用户直接驳回："*逻辑很简单的事情全被你搞复杂！？*" |
| 在 `OnTurnEndInHand` 里查 `Pile?.Type` 判"在不在手牌" | 不在手牌的牌**根本进不了** `turnEndCards`，回调压根不触发 ⇒ 分支永远走不到（实机日志实锤：该分支一次都没出现过） |

#### 一句话记牢

> **先拆"什么事件触发"，再去原版找那个事件已经在哪处理 —— 十有八九，原版已经有一张牌
> 或一个关键词干着同样的事。照着它 override 就行，别自己造轮子。**

---

## 第 1 章　构建环境

### 1.1 用 MegaDot，不是官方 Godot

StS2 用的是 **Mega Crit 自制的 Godot 分支**（[megadot.megacrit.com](https://megadot.megacrit.com/)），当前对应 **Godot 4.5.1**。

⚠️ 别照抄别人 csproj 里的 `Godot.NET.Sdk/4.5.2` —— 那是作者本地环境漂移。
**判断正确版本的硬证据**：模组 `.pck` 文件头里写着引擎版本（见 1.4），
游戏装的所有 `.pck` 都标 `godot=4.5.1`。

### 1.2 官方脚手架

```bash
dotnet new install Alchyr.Sts2.Templates
dotnet new alchyrsts2mod      --ModAuthor 你的名字 -o 模组名   # 最小模板
dotnet new alchyrsts2charmod  --ModAuthor 你的名字 -o 模组名   # 角色模板
dotnet new alchyrsts2contentmod --ModAuthor 你的名字 -o 模组名 # 内容模板
```

约定：**项目名不含空格/下划线**；**必须是 `.sln` 不是 `.slnx`**（Godot 要求）。

### 1.3 Build vs Publish —— 最容易被忽略的一条

| 操作 | 做了什么 | 什么时候用 |
|---|---|---|
| **Build**（锤子） | 编译 `.dll`，拷到 mods 目录 | **只改了 `.cs`** 时 |
| **Publish** | 编译 dll + **调 Godot 生成 `.pck`** + 拷三件套 | **改了任何非代码文件**（文案/图片/场景） |

> **官方原话**：`You must publish your mod to a local folder every time you make any non-any-code changes (text, images, scenes, etc.) for their show up.` ——**只 Build 不够**。

⚠️ **我在这个坑上反复吃亏**：改完本地化只想"重编译一下"，结果文案不更新。
因为 `.pck` 是我手工生成的（见 1.4），我养成了"改 dll 就完事"的坏习惯。

### 1.4 `.pck` 是什么，能不能手工造（本项目自研）

`.pck` 里装的是**非代码资源**。本地化走 `res://<mod_id>/localization/<lang>/<表>.json`，
而 `res://` 必须有挂载点 → **所以本地化必须有 `.pck`**（旁证：所有带本地化的模组都有 pck；
唯一无 pck 的 `SkinChanger` 恰好不带本地化）。

**实测出的 packFormat 3 格式**（依据本机 `BaseLib.pck` / `BonModConfig.pck` 反推）：

```
Header 40B: u32 magic=0x43504447 "GDPC"; u32 packFormat=3; u32 4,5,1; u32 flags=0x2;
            u64 filesBase; u64 dirOffset
Dir:  u32 count; 每项: u32 pathLen(含'\0'); utf8 path+'\0';
                       u64 offset(相对 filesBase, 16B 对齐); u64 size; u8[16] md5; u32 entryFlags=0
```

- **`flags=0x2` 表示偏移相对 filesBase**（实测两个 pck 的最小偏移都是 0，小于 filesBase）
- **顶层目录名用 manifest 的 `id`**，不是 `pck_name`（`BonModConfig` 的 `id` 与 `pck_name` 故意不同，用的是 `id`）
- 每个文件的数据 16 字节对齐

**手工造 pck 完全可行**（本项目 `_tools\MakePck` 就是），好处是不必装 Godot；
代价是要自己保证格式正确。**验证方法**：写完用独立解析器反读，逐项核对 path / size / MD5 / 数据端到端一致。

### 1.5 离线构建（无网络时）

- 把游戏 dll（`sts2.dll` / `0Harmony.dll` / `GodotSharp.dll`）、`BaseLib.dll` 复制进工程旁边的 `_refs\`
- 把 NuGet 缓存里的 `godot.net.sdk` / `godot.sourcegenerators` / `godotsharp` / `godotsharpeditor` 的 **`.nupkg`** 收进一个目录当**本地 feed**，`nuget.config` 里 `<clear/>` 后只指向它
- ⚠️ **不要**为了绕开 SDK 而改成裸 `<Reference Include="GodotSharp">`：那样能编译，但会**丢掉 Godot 源生成器**，运行期 `partial class : Node` 每帧抛 `ArgumentException`

### 1.6 常见构建报错对照

| 报错 | 原因 / 解法 |
|---|---|
| `MSB4236: The SDK 'Godot.NET.Sdk/4.5.1' specified could not be found` | 加回 nuget 源：`dotnet nuget add source https://api.nuget.org/v3/index.json` |
| `Slay the Spire 2 data not found at path '?/steamapps/...'` | 在 `Directory.Build.props` 里取消注释并设置 `<Sts2Path>` |
| `Value does not fall within the expected range` + 一长串 `MonoMod.Core.Interop...InvokeCompileMethod` | **Publicizer 的坑**：要么把 csproj 里 `<Publicize Include="sts2" ...>` 上方的 `Condition="False"` 改成 `True`，要么**整个移除 `Krafs.Publicizer` 包** |
| `Undefined resource string ID:0x80070057` | 同上（Publicizer） |
| Publish 不工作 | 在 GodotPublish 命令前加 `DOTNET_ROOT=<dotnet 路径>` |

---

## 第 2 章　Mod 怎么被加载

### 2.1 加载顺序与目录

游戏扫两个地方：
1. **游戏根 `mods\<任意子目录>\<ModName>.json`**（本地模组）
2. **工坊 `steamapps\workshop\content\2868840\<id>\`**（Steam 模组）

三件套：`<ModName>.dll` + `<ModName>.pck`（可选）+ `<ModName>.json`（manifest）。
**`id` 字段决定文件名**，别乱改。

### 2.2 ⚠️ 本地 mods 目录里的模组**默认不启用**

游戏把模组启用状态存在
`%APPDATA%\SlayTheSpire2\steam\<steamid>\settings.save` 的 `mod_settings.mod_list[]`（`id` / `is_enabled` / `source`）。

- **工坊模组**：一般自动启用
- **本地 `mods\` 目录的模组**：**必须手动在 设置→Mods 里勾选**，否则日志里会出现
  `Skipping loading mod X, it is set to disabled in settings`

### 2.3 manifest 字段

```json
{
  "id": "MyMod",                 // 决定文件名，不要改
  "name": "显示名",
  "author": "...",
  "description": "...",
  "version": "1.0.0",            // ← 联机校验用，见 2.4
  "has_pck": true, "has_dll": true,
  "min_game_version": "0.111.0", // 可选，建议加
  "dependencies": [{ "id": "BaseLib", "min_version": "3.4.5" }],
  "affects_gameplay": true
}
```

### 2.4 ⚠️ 联机版本串：一个字符都不能差

base game 用 `<mod_id>-<version>` 拼字符串校验双方 mod 列表。
任何差异（**包括 `v` 前缀、点号位置**）都会 `ModMismatch` 拒绝入房。
→ 升级后所有玩家必须**整目录替换**，不存在协议层兼容。

---

## 第 3 章　Harmony 实战

### 3.1 Priority 是**常量不是枚举**，且 postfix 升序执行

`HarmonyLib.Priority` 是静态常量类（实测值）：

```
Last=0  VeryLow=100  Low=200  LowerThanNormal=300  Normal=400
HigherThanNormal=500  High=600  VeryHigh=700  First=800
```

**多个 patch 打同一方法的执行顺序**：priority 小者**先**执行（前缀和后缀都按升序）。
需要"我必须最后跑"时，给一个比别人大的值，并**在注释里写下为什么**。

### 3.2 逐类隔离 patch（强烈推荐）

`Harmony.PatchAll()` 一旦某个 patch 类目标解析失败，会**抛异常并中断剩余所有 patch 的应用**——
一个坏 patch 能让后面所有 patch 全部失效，而且你只看到一条错。

**正确做法**：对每个类型单独 `CreateClassProcessor().Patch()` 并各自 try/catch：

```csharp
var harmony = new Harmony(ModId);
foreach (var type in Assembly.GetExecutingAssembly().GetTypes()) {
    try { harmony.CreateClassProcessor(type).Patch(); }
    catch (Exception ex) { Logger.Error($"Patch class {type.FullName} failed (skipped): {ex}"); }
}
```

好处：坏 patch 只记日志、跳过，其余照常工作，且**日志能直接告诉你是哪个类坏了**。

### 3.3 patch 永远不要抛给调用者

Harmony prefix/postfix 抛异常会破坏整条 patch 链，别的 mod 会收到莫名其妙的异常。
**统一入口包一层**：

```csharp
public static void Run(string name, Action body) {
    try { body(); } catch (Exception ex) { Logger.Error($"[{name}] suppressed: {ex}"); }
}
// prefix 需要 return bool 的场景给个 fallback=true（放行原方法）
public static T Run<T>(string name, Func<T> body, T fallback = default!) { ... }
```

⚠️ 注意：`ref` / `out` 参数**不能进 lambda**，要用局部变量承载结果再赋回。

### 3.5 ⚠️ **属性不能用 `nameof(属性)` 当 patch 目标**（本轮血的教训）

`[HarmonyPatch(typeof(X), nameof(X.SomeProperty))]` 会在**注册阶段**就失败，
而且**不会**让 mod 崩 —— 只是这个 patch 类被跳过，功能静默不生效：

```
[ERROR] Patch class …ActMapTopBgThemePatch failed (skipped):
  HarmonyException: Patching exception in method null
  ---> System.ArgumentException: Undefined target method for patch method
       static Void …ActMapTopBgThemePatch::Postfix(ActModel __instance, Texture2D& __result)
```

原因：`nameof(属性)` 给出的是**属性名**（`Title` / `MapTopBg` / `RoomType`），
而元数据里真正的成员是**编译生成的 getter**（`get_Title` / `get_MapTopBg` / `get_RoomType`）。
Harmony 的注解解析按**方法名**查找 → 找不到 → 整个 patch 类被跳过。

**正确写法**（显式方法名，或手动给 TargetMethod）：

```csharp
[HarmonyPatch(typeof(ActModel), "get_Title")]          // ✅
[HarmonyPatch(typeof(CombatRoom), "get_RoomType")]     // ✅
// [HarmonyPatch(typeof(ActModel), nameof(ActModel.Title))]   ❌ 永远不生效
```

**排查方法**：每次启动后 grep 日志里的 `failed (skipped)`：

```powershell
Select-String -Path "$env:APPDATA\SlayTheSpire2\logs\godot.log" -Pattern 'Patch class .* failed \(skipped\)'
```

**本项目实际代价**：`CombatRoom.RoomType`、`ActModel.Title`、`ActModel.MapTopBg/MidBg/BotBg`
共 5 个 patch 全部静默失效 —— 表现成"改了代码但游戏里毫无变化"，
我因此**误判过两次**（以为设计不对，其实是 patch 根本没挂上）。
### 3.4 patch 目标的存在性必须校验

Harmony 目标是**字符串绑定**，编译期查不出。目标没了 → patch 静默失效。
**做法**：写脚本把源码里所有 `[HarmonyPatch(typeof(X), nameof(Y.M))]` 抽出来，
对程序集元数据逐条校验「类型存在 + 成员存在」，并在报告里区分"真正缺失"与"改名/迁移"。
（本项目 40 个目标全绿，靠的就是这个脚本。）

---

## 第 4 章　游戏 API 的硬事实（都是实测）

### 4.1 程序集与类型

- 主程序集：`data_sts2_windows_x86_64\sts2.dll`（约 9.7MB）
- 版本：`游戏根\release_info.json` → `version` / `branch`
- 引擎：MegaDot = Godot 4.5.1
- 联机/模组：`MegaCrit.Sts2.Core.Modding.ModManager`

### 4.2 虚方法 vs 非虚方法（决定你能否 override）

| 成员 | 可 override？ |
|---|---|
| `ActModel.BaseNumberOfRooms` | ✅ `protected virtual` |
| `ActModel.GetNumberOfRooms(bool)` | ❌ **不是 virtual** → 只能 Harmony postfix |
| `ActModel.CreateMap(RunState, bool)` | ✅ 可 override（也被 `ActModel.CreateMap` patch 打） |
| `ActMap.Grid` | ✅ 但**是 `protected`**（外部只能 `get_Grid()`） |
| `ActMap.BossMapPoint` / `SecondBossMapPoint` / `StartingMapPoint` | ✅ 抽象，自定义地图必须实现 |

### 4.3 地图与房间

- **`ActModel.CreateMap(RunState runState, bool replaceTreasureWithElites) → ActMap`** 是建图的唯一入口。
  **要自定义地图就 override 它**（配合 prefix patch 在建图阶段接管），
  ⚠️ **不要**"先让原版建图、再事后改写 `map.Grid`"——见 4.5。
- `MapPointType` 枚举值：`Unassigned=0 / Unknown=1 / Monster=2 / Elite=3 / RestSite=4 / Shop=5 / Boss=6 / Treasure=7 / Ancient=8`

#### 4.3.1 ⚠️ 节点类型的**语义陷阱**（我在这上面做错了三次）

`MapPointType` 不只是"画什么图标"——它同时决定**图标**、**取怪通道**、**是否可进入**。
把这三件事当成一件事，就会连环出错。实测踩到的三种：

| 我设置的 | 实际结果 | 原因 |
|---|---|---|
| 全部 `Boss` | 每场都是**同一个 boss** | 所有节点都走 `PullNextEncounter(RoomType.Boss)` → `NextBossEncounter` → 直接返回 `RoomSet._boss` |
| 前 N-1 个 `Monster` + 末端 `Boss` | 前几个显示**小怪图标**；末端**错误图标且不可进入** | `Monster` 就是小怪图标；而 `Boss` 类型节点需要与该层 `BossMapPoint` 的绑定关系才渲染得出来 |
| 自己 `new MapPoint(...)` 塞进 `BossMapPoint` | `ObjectDisposedException` / 黑屏 | 见 4.5.2：`BossMapPoint` 必须是原版建图时创建的实例 |

**正确的思考顺序（三者解耦）**：

1. **图标** —— 要 BOSS 图标就把节点设成 `MapPointType.Boss`；要小怪图标用 `Monster`；黄色精英用 `Elite`
2. **取怪** —— **不要依赖节点类型**。把你想让他按顺序打的怪写进 `RoomSet.normalEncounters`，
   并**同时归零 `normalEncountersVisited`**（见 4.4）。这样每场都不同，且与图标无关
3. **可进入** —— 节点必须在网格里（`grid[col,row] = point`），并被 `NMapScreen._mapPointDictionary` 收录

#### 4.3.2 「每层必有一个主 Boss 点」

从 `ActModel.CreateMap` → `StandardActMap.CreateFor` 的 IL 确认：

```csharp
StandardActMap.CreateFor(runState, replaceTreasureWithElites) {
    hasSecondBoss: runState.Act.HasSecondBoss      // ← 只有第二个 boss 点是"可选"的
}
```

- **主 boss 点（`BossMapPoint`，= 通关触发点）必然创建**，每个 act 都有
- **第二个 boss 点（`SecondBossMapPoint`）可选**，由 `ActModel.HasSecondBoss` 决定
- 这三个都是 `ActMap` 的**抽象属性**，自定义 `ActMap` 子类必须实现

**推论**：想让多个节点都显示 BOSS 图标，就都设 `MapPointType.Boss`；
但 **`BossMapPoint` 只能指向其中一个（通常是最后那个 = 通关触发点）**，
其余 `Boss` 节点靠网格+字典渲染、靠 `normalEncounters` 取怪。

#### 4.3.3 ⚠️ 不要手工 `AddChildPoint` 连边

`MapPoint.AddChildPoint` 是**双向**的（同时写 `Children` 和私有 `parents`），
但**原版 `StandardActMap` 从不调用它** —— 它只做三步后处理：

```
MapPostProcessing.CenterGrid → SpreadAdjacentMapPoints → StraightenPaths
```

**相邻关系是从网格布局推导的。** 我手工连边的后果：
`NMapScreen.DrawPaths` 对每条边做**无检查的 `Dictionary.Add`** → 重复边直接抛
`ArgumentException: An item with the same key has already been added`（见 4.5.3）。

**所以：只用 `grid[col,row] = point` 摆位置就够了，别连边。**

- `MapPoint.coord` 私有且**无 setter**；`new MapPoint(col,row)` 会忽略参数。
  落位靠 `grid[col,row] = point`（IL 里的 `MapPoint[,]::Set`），必要时反射写 `coord` 私有字段。

### 4.4 取怪：`PullNextEncounter` 与 `RoomSet`

`ActModel.PullNextEncounter(RoomType)` 的 IL 是**纯三路转发**：

```
RoomType.Monster / Elite → RoomSet.NextNormalEncounter   // normalEncounters[normalEncountersVisited % Count]
RoomType.Elite           → RoomSet.NextEliteEncounter
RoomType.Boss            → RoomSet.NextBossEncounter     // 直接返回 _boss
其他                      → ArgumentOutOfRangeException
```

`RoomSet` 的私有字段：`normalEncounters` / `normalEncountersVisited` /
`eliteEncounters` / `eliteEncountersVisited` / `bossEncountersVisited` / `_boss` / `events` / `eventsVisited` / `_ancient`。

**⚠️ 血泪教训**：**替换了 `normalEncounters` 列表，就必须同时把 `*Visited` 计数器归零**。
否则 `visited % Count` 会落在错误偏移上，表现为"每场都打同一个怪"。

### 4.5 ⚠️⚠️ 最贵的一课：不要在**已创建的地图**上做事后改造

我在这上面连续失败三次，症状从 `ObjectDisposedException: 'NBossMapPoint'` 到
`ArgumentException: 同一键已添加 Key:(MapCoord(3,0), MapCoord(3,1))`。

**根因**：`NMapScreen.SetMap` 会沿着 `map.BossMapPoint` 调 `Godot.Node.GetPath()` 建视觉节点。
你在 act 切换时改写地图，触碰的是**已经被 dispose 的对象**。

**两条正确路线**：

1. **建图前接管**：override `ActModel.CreateMap` 或 patch 它的 prefix，返回一个**自定义 `ActMap` 子类**。
   原版地图压根不生成，就不存在"动别人的已释放对象"。
2. **只做追加**（工坊模组 `Boss Gauntlet` 的手法，见第 6 章）：原版地图一个字节不动，
   在**地图外的虚拟坐标**新建节点，自己画、自己接管可通行性与进入。

### 4.6 进度与历史

- `RunState.MapPointHistory` 是 **`List<List<MapPointHistoryEntry>>`**（按 act 分组，必须两层遍历）
- 每个 entry：`MapPointType` + `Rooms: List<MapPointRoomHistoryEntry>`（含 `RoomType` / `ModelId` / `MonsterIds` / `TurnsTaken`）
- **官方也是这么数的**：`ScoreUtility.GetElitesKilledCount(history)`
- ⚠️ `MapPointHistory` **只记录"已走过"的节点**。想知道"哪些精英没打"，只能用**池差集**
  （全部池 − 打赢过的），拿不到"看到但绕开"的精确计数。

### 4.7 爬塔层数：`ActFloor` vs `TotalFloor`

`RunState` **同时有** `ActFloor`（act 内层数）和 `TotalFloor`（全局累计）。别搞混。
实测：act4 内 `ActFloor=14`（≈该层房间数），显然是 act 内口径。

### 4.8 双 boss / 进阶 10

- `AscensionLevel.DoubleBoss`（=10）
- `ActModel.HasSecondBoss` ← 由 `SecondBossEncounter != null` 决定
- `ActModel.SetSecondBossEncounter(...)` / `ActMap.SecondBossMapPoint`
- ⚠️ 游戏在 `RunManager.GenerateRooms` 里把双 boss **写死在"最后一个 act"**
  （`actIndex == Acts.Count - 1 && HasLevel(10)`）。**追加 act 的 mod 会让它顺延**。

### 4.9 `AllEncounters` 是 lazy 缓存

`ActModel.AllEncounters` = `_allEncounters ?? 生成 + 缓存`，
**只在 mod 加载时跑一次**。所以"运行时可变的过滤"（依赖 run 进度、玩家开关）
**绝不能**放进 `GenerateAllEncounters` —— 那样第二次 run 也不会重算。
放到每次 run / 每层都会跑的地方（如 `RunManager.GenerateRooms` postfix）。

### 4.10 音频：**"按角色"的槽位只有 6 个，格挡音不在其中**

**这是 2026-09-22 被用户抓出来的坑**（原话：*"银龙的声音导致了其他角色的技能牌也出现了变调"*）。

`CharacterModel` 上**能按角色覆盖的音效槽位一共就这些**（`_api\sts2.api.txt` 实据）：

```
CharacterSelectSfx / AttackSfx / CastSfx / PowerUpSfx / DeathSfx / CharacterTransitionSfx
```

**没有"格挡音"槽位。** 格挡音是硬编码的 —— 反编译 `CreatureCmd.GainBlock`：

```csharp
public static async Task<decimal> GainBlock(Creature creature, decimal amount, ValueProp props, CardPlay? cardPlay, bool fast = false)
{
    ...
    await Hook.BeforeBlockGained(combatState, creature, amount, props, cardPlay?.Card);
    modifiedAmount = Hook.ModifyBlock(combatState, creature, modifiedAmount, props, cardPlay?.Card, cardPlay, out ...);
    await Hook.AfterModifyingBlockAmount(combatState, modifiedAmount, cardPlay?.Card, cardPlay, modifiers);
    if (modifiedAmount > 0m)
    {
        SfxCmd.Play("event:/sfx/block_gain");     // ← 硬编码 ldstr：所有角色 + 敌人共用同一个事件
        VfxCmd.PlayOnCreatureCenter(creature, "vfx/vfx_block");
        ...
    }
    await Hook.AfterBlockGained(combatState, creature, modifiedAmount, props, cardPlay?.Card);
}
```

两条硬结论：

1. **`block_gain` / `block_break` / `block_hit` 是没有角色前缀的全局事件**
   （对比 `event:/sfx/characters/{id}/{id}_attack` 那一套）
   ⇒ 在播放层无脑替换 = **全局改动**，别的角色（连敌人）一起变。
2. **音效在 `Hook.AfterBlockGained` 之前就播了** ⇒ 想用钩子"拦下来"是拦不住的，
   只能在播放层（`SfxCmd.Play` / `NAudioManager.PlayOneShot`）或 `GainBlock` 本身动手。

**配套的两个坑**：

- **变调会叠两次**：如果离线把音频重采样成 ×0.85（低沉）再在播放时套 `PitchScale = 1.35`，
  净效果是 **×1.1475 —— 比原版还高**，把"低沉"完全抵消还倒过来。
  **凡是已经烘进文件的变调，播放时必须走原速**（本项目放在 `OrcaAudio.PitchFor` 白名单里）。
- **别用静态 bool 缓存"现在玩的是不是奥卡"**：程序集常驻进程，
  "同一个进程里打完奥卡再开别的角色"会让标志**过期**⇒ 泄漏又回来了。**每次现查**。

**静态上下文里怎么拿本地玩家**（`NAudioManager.PlayOneShot` 这种静态补丁里没有 `Player` 参数）：

```csharp
var players = RunManager.Instance?.DebugOnlyGetState()?.Players;   // RunState.Players : IReadOnlyList<Player>
bool isOrca = players != null && LocalContext.GetMe(players)?.Character is Orca;
```

- `RunManager.State` 是 **private**，公开出来的取值方法叫 `DebugOnlyGetState()`
  —— 反编译实据：它本体就是 `return State;`，纯取值、无副作用，只是名字带 Debug。
- 失败一律 **返回 false**（退回原版）—— 宁可不换，也不误伤别的角色。

---

## 第 5 章　⚠️ 实例身份：一个反复咬人的坑

**`GenerateRooms(ActModel __instance)` 的 `__instance` 不一定等于 `state.Act`。**

实测日志（本项目）：

```
[Act5] 实例校验: __instance==57983585  state.Act==52090220  同一实例=False
                 state.Act.normalEncounters=15      ← 15 是 act4 的池大小！
```

含义：跑 `GenerateRooms` 时，`state.Act` **还指向上一层**（act 切换尚未完成）。
你在这里改 `__instance` 的 `RoomSet`，改的是"模板实例"，
而战斗用的是**运行时实例** → **写入完全无效**（表现为"每场都打同一个 boss"）。

**对策**：
1. 需要"改运行中 act"时，**先校验 `ReferenceEquals(__instance, state.Act)`**，并在日志里打印这个布尔值；
2. 时机选在 **`state.Act` 已经切过去之后**（例如 `RunManager.GenerateMap` 的 postfix，或 `SetActInternal` 之后）；
3. 更稳的做法：**直接对 `state.Act` 写入**，而不是对 patch 的 `__instance`。

---

## 第 6 章　往地图里插节点：`Boss Gauntlet` 的手法（IL 拆解）

来源：工坊 `BossGauntlet` v0.1.3（作者 婉拒菲尔兹，目标 v0.111.0）。
**它解决"两个 boss 之间插入火堆/商店"，而且实测可用** —— 值得照搬。

### 6.1 核心思想

> **保留原版地图一个字节不动，在「地图外的虚拟坐标」新建合成节点，自己画、自己接管可通行性、自己接管进入。**

### 6.2 关键私有字段（用 `HarmonyLib.Traverse` 取）

`NMapScreen` 上：

| 字段 | 类型 |
|---|---|
| `_bossPointNode` / `_secondBossPointNode` | `NBossMapPoint` |
| `_points` | `Control`（节点容器） |
| `_mapPointDictionary` | `Dictionary<MapCoord, NMapPoint>` |
| `_paths` | `Dictionary<(MapCoord,MapCoord), IReadOnlyList<TextureRect>>` |

### 6.3 合成坐标与节点

```csharp
// 恒定 +50 偏移，越出正常地图范围
int col = map.BossMapPoint.coord.col         + 50 + index;
int row = map.SecondBossMapPoint.coord.row   + 50 + index;

var p = new MapPoint(col, row);
p.PointType = type;          // RestSite / Shop ...
p.CanBeModified = false;
```

### 6.4 第二个 boss 的选择（**兼容其他模组的正确姿势**）

```
候选 = act.AllBossEncounters
        .Where(b => b != act.BossEncounter)          // 排除首个
        .OrderBy(b => b.Id.Entry, StringComparer.Ordinal)   // 稳定顺序
index = GetStableBossIndex(runState, act, actIndex, 候选数)
act.SetSecondBossEncounter(候选[index])
```

- **用 `act.AllBossEncounters`** → **别的模组注册进该 act 的 boss 自动成为候选**（这是兼容性的关键）
- **确定性选择**：把 `RunRngSet.StringSeed | act.Id | act.BossEncounter.Id | actIndex | 候选数`
  拼成字符串做散列 → 得到 [0, 候选数) 的索引。
  **绝不调用 `rng.NextItem(...)`** —— 不消耗 rng 流，host/client 各自算都一致，也不打乱后续随机序列。
- 已经 `HasSecondBoss` 就不覆盖（尊重别人安排的）

### 6.5 视觉注入（`NMapScreen.SetMap` postfix）

```
1. Traverse 取上面 5 个字段；取不到 → Log.Error 并放弃
2. RemovePath((BossMapPoint.coord, SecondBossMapPoint.coord), _paths)
   ← 先删掉原来那条直连，否则两条线重叠
3. start = bossNode.Position + bossNode.Size*0.5;  end = secondBossNode.Position + ...
4. 对每个要插的房间类型 i：
     pt   = CreateSyntheticPoint(map, i, roomTypes[i])
     node = NNormalMapPoint.Create(pt, mapScreen, runState)   // ★ 公开静态工厂，别自己造节点
     _points.AddChild(node)
     node.Position = Vector2.Lerp(start, end, (i+1)/(n+1)) - node.PivotOffset
     node.Scale = Vector2.One * factor
     _mapPointDictionary[pt.coord] = node        // ★ 索引器赋值，不是 Add
5. 重连：BossPoint → 合成点 → … → 最后一个合成点 → SecondBossPoint（AddChildPoint）
6. 逐条调 NMapScreen.DrawPaths(节点, MapPoint) 画连线
7. SetSyntheticFocusNeighbors(firstBoss, secondBoss, nodes)  // 键盘/手柄焦点
```

### 6.6 还要接的四个流程（**别以为画出来就完事**）

| 流程 | patch 点 |
|---|---|
| 进入合成节点 | `RunManager.EnterMapCoord` prefix（自己造房间并推进） |
| 第一个 boss 打完的"继续" | `RunManager.ProceedFromTerminalRewardsScreen` prefix |
| 读档落在合成节点上 | `RunManager.LoadIntoLatestMapCoord` prefix |
| 多人投票坐标 | `MapSelectionSynchronizer.PlayerVotedForMapCoord` prefix |
| 离开合成房间 | `NRestSiteRoom.OnProceedButtonReleased` / `NMerchantRoom.HideScreen` prefix |

还配了 `NMapScreen.RecalculateTravelability` postfix 补可通行性。

### 6.7 它明确不碰的

- **不 patch 房间生成**、**不 patch act 选择**
- 只在"当前 act 的地图即将生成时"赋第二个 boss（`RunManager.GenerateMap` prefix）
- ⚠️ README 警告：**`Custom map replacements that omit SecondBossMapPoint are not supported`**
  → 做自定义地图时必须提供 `SecondBossMapPoint`
- ⚠️ 不要和 AscensionPlus 同时开（它的 Act2 A20 火堆 patch 打同一个 transition，会产生重复合成节点）

---

## 第 7 章　配置与本地化

### 7.1 BaseLib 配置系统

```csharp
internal partial class MyConfig : SimpleModConfig {
    [ConfigSection("General")]  public static bool Enabled { get; set; } = true;
    [ConfigSlider(0, 20, 0.05)] public static double Scale { get; set; } = 1.0;
    [ConfigVisibleIf(nameof(Enabled))] public static double Child { get; set; }
    [ConfigSyncIgnore] public static bool LocalOnly { get; set; }   // 不参与联机同步
}
```

- 注册：`ModConfigRegistry.Register(ModId, new MyConfig());`
- 落盘 + 刷 UI：`cfg.Save(); cfg.ConfigReloaded();`

### 7.2 ⚠️ 本地化表的键名有**两套**，必须都对上

BaseLib 的运行期本地化表 `settings_ui` 由 `LocManager` 按 `res://<id>/localization/<lang>/<表>.json` 加载。

- **字段的文案键**：`NOTENOUGHDIFFICULTY-<字段名的 UPPER_SNAKE>.title` / `.description`
  - ⚠️ 大小写转换规则是 `Act1_ExtraScaling → ACT1_EXTRA_SCALING`（不是 `A_CT1_...`）
- **代码里手写的键**（例如注入到游戏官方设置界面的行）：**必须与代码里的字符串逐字一致**

**我踩过的坑**：注入行用 `NOTENOUGHDIFFICULTY-ENABLE_EXTRA_SPEED.title`，
而配置字段派生的键是 `NOTENOUGHDIFFICULTY-ENABLE_SPEED_MULTIPLIER.title`。
我只写了后者（或只写了前者），于是日志出现
`Key 'NOTENOUGHDIFFICULTY-ENABLE_EXTRA_SPEED.title' not found in table 'settings_ui'`。
**两套键都要写。**

**校验方法**：写脚本把所有 `public static ... { get; set; }` 配置字段转成期望键，
与 JSON 的键集合比对，报告缺失项。

### 7.3 `.description` 就是"配置注释"

BaseLib 把 `.description` 作为悬停提示显示。**每个配置项都该有一句人话说明**——
否则玩家看不懂（我的第一版就是被用户指出"很多选项没有注释"）。

### 7.4 ⚠️ 登记制：自定义本地化表必须注册

新版 BaseLib 只有**被注册过的表名**才会进入加载列表：

```csharp
foreach (var table in new[] { "settings_ui.json", "acts.json", "cards.json" })
    CustomLocTableManager.Register(table);
```

而且**时机有坑**：`LocManager` 首次加载后会缓存。若你的注册发生在它加载之后，
要用当前语言强制 `SetLanguage(lm.Language)` 重建缓存，否则 UI 显示原始 key。

### 7.5 多语言自动切换

游戏按当前语言选 `localization/<lang>/` 目录，**你只要把每个语言的 JSON 都提供就行**。
（当前客户端 `language: zhs` → 看中文；英文客户端自动看 `eng/`。）

---

## 第 8 章　多 mod 共存：**运行时检测 + 放权**

玩家往往装几十个 mod（本项目实测环境有 50+）。两个 mod 抢同一个 patch 点，
会产生**极难定位**的怪现象（功能互相覆盖、节点重复、数值错乱）。
靠硬编码"冲突模组名单"不可行——名单永远不全，而且更新就失效。

### 8.1 正确做法：问 Harmony「这方法上还挂了谁」

Harmony 维护着"每个被 patch 的方法上挂了哪些 patch"，每条 patch 带 `owner`（发起 patch 的 Harmony id）。
**你的 mod 用 `ModId` 当 Harmony id**，于是：

```csharp
var info = Harmony.GetPatchInfo(target);      // target = AccessTools.Method(typeof(X), "Y")
// info.Prefixes / Postfixes / Transpilers / Finalizers，每条都有 .owner
// owner != 我们 且 不在"预期依赖"里（BaseLib/Harmony自身）→ 就是别的 mod
```

本项目实现见 `Core/ModCompat.cs`，提供：

```csharp
ModCompat.SomeoneElsePatchesRoomGeneration("双 boss 分配")   // -> true 表示有人跟我们抢
```

### 8.2 放权策略（本项目采用）

> **检测到别的 mod 也在改同一处 → 直接跳过自己的功能，并记一条清晰的日志。**
> 宁可功能不生效，也不要在多 mod 环境里互相打架。

日志必须写清"谁在抢、我们跳过了什么"，否则玩家只会看到"功能没生效"：

```
[ModCompat] 双 boss 分配: 检测到其它 mod 也在改 RunManager.GenerateRooms [SomeOtherMod]
            —— 本功能放权跳过，避免打架
```

### 8.3 还要在启动时打一份**兼容性报告**

单点放权之外，开局把所有关键 patch 点上的"其它 mod"列一遍，
让"功能为什么没生效"一眼可查：

```
[ModCompat] 关键 patch 点上的其它 mod：
  GenerateRooms: [ModA, ModB]
  CreateMap:     []
  SetMap:        [BossGauntlet]
```

### 8.4 三类兼容性，别只做第一类

| 类型 | 做法 |
|---|---|
| **池子兼容** | 从 `act.AllBossEncounters` / `ModelDb.ActsByIndex` 这类**游戏自己的池**取内容，别硬编码名字；别的 mod 注册进去的东西自动可用 |
| **选择兼容** | 已存在的内容不要覆盖（如 `if (act.HasSecondBoss) 跳过`），并**在日志里说明是别人安排的** |
| **时序兼容** | 用 `[HarmonyPriority]` 明确先后，并在注释里写下为什么（见 3.1） |

### 8.5 ⚠️ 放权逻辑自身的风险

**放权条件是"有别人 patch 这个方法"，太宽会把你自己关掉。**
例如：另一个纯 UI mod 恰好在 `RunManager.GenerateRooms` 上挂了个无关的 postfix，
你的核心功能就会整体静默失效。

**缓解**：
1. 放权日志必须足够显眼（列出 owner 名），便于一眼判定；
2. 关键功能提供"强制启用"的配置开关；
3. 只对**真正会被覆盖**的点放权，不要无脑全放。

### 8.6 ⚠️⚠️ 8.5 预警的实例："双BOSS中间火堆没有了"（2026-09-22）

**现象**：装了创意工坊模组 **Ascension100**（Steam `3801607408`）之后，第一层地图上两个 BOSS
之间那个合成火堆**直接消失**了（双 boss 本身还在，所以看起来像"火堆功能被删了"）。

**取证链（每一环都有实据，别跳步）**：
1. 玩家的游戏日志里 `[SynthHearth]` **0 次命中** —— 但 `DebugLogging` 默认关，所以"没日志"什么都不能证明；
2. 把当天日志与前几天对比：`Ascension100` 在 09-19、09-21 的日志里 **0 命中**，09-22 的日志里 14~15 命中
   ⇒ 它是 09-21 23:20（workshop 目录创建时间）**新装/新更新**的，时间点与 bug 出现完全吻合；
3. 用本项目 `_tools\Decomp` 反编译 `Ascension100.dll`：它 `PatchAll(assembly)`，其中
   `Ascension100.Ascension11.MapRefreshPatch` 是 **`[HarmonyPatch(typeof(NMapScreen), "SetMap")]`**
   （只为"进阶 11 的地图火焰特效"挂一个纯视觉 postfix）；同类里还有
   `[HarmonyPatch(typeof(RunManager), "GenerateRooms")]`（进阶 18 的"第二幕双 boss"）；
4. 我们的 `NMapScreenSetMapPostfix` 开头有一句
   `if (ModCompat.SomeoneElsePatchesMapScreen("合成节点注入")) return;`
   ⇒ **整块注入（合成火堆 + act5 伪装 BOSS 节点）被一票否决**。而双 boss 还在，是因为
   `RunManager.GenerateMap` 前缀那条补救路径**没有**放权判定，它在建图前把第二 boss 补上了。

**结论（写进代码注释了）——把接缝分成"放权"与"共存"两类**：

| 接缝性质 | 判据 | 做法 |
|---|---|---|
| **我们只做加法**（往容器里多挂节点、多注册内容、只读诊断） | 别人的 patch 改的都是"同一个方法的别的事" | **共存**：只记一条 Info 日志列出 owner，**继续执行** |
| **我们会覆盖同一个事实**（同一个 act 的第二 boss、同一个池子的内容） | 双方都在写同一个字段/同一个列表 | 才放权；且必须先看对方是否已经自带"已存在就跳过"的礼让（8.4 选择兼容） |

**日志级别的硬要求**：放权/共存的判定**必须用 `Logger.Warn`/`Info`**，不能用"默认关"的调试日志
——这次正因为成功与跳过都只走 `DebugLog`，日志里一个字都没有，现象看起来像"代码没写"。
反过来，**关键功能的成功路径也应该用 Info 打一条**（"已在第 N 层注入 1 个合成节点（RestSite…）"），
让玩家不开调试也能一眼确认功能到底有没有生效。

**顺带记下的两个反例事实**（以后别再踩）：
- `Harmony.GetPatchInfo` 看到的是"**有人挂了 patch**"，**不是**"有人做了和你一样的事"；
  Ascension100 的那个 postfix 甚至在**延迟一帧**才跑，和我们后注入的节点完全不冲突
  （它遍历整棵树给 `NNormalMapPoint` 挂火焰，我们新插的火堆节点照样有火焰）。
- 同一类事故还有第二个受害者：`RunManager.GenerateRooms` 上的放权会让**按层双 boss 开关整体失效**
  （日志 `[ModCompat] 双 boss 分配: … RunManager.GenerateRooms [Ascension100] —— 放权跳过`）。
  它和 Ascension100 的进阶 18 **其实是礼让型**的（双方都 `if (act.HasSecondBoss) 跳过`），
  放权反而两边都不做——这正是 8.5 说的"放权条件太宽"。

**当日处置（可作为模板抄）**：

1. `SetMap`（我们只做加法）→ 改**共存**：`ModCompat.NoteCoexistence(...)`，记一条 Info 后照常执行；
2. `GenerateRooms`（双方都在写"这一层有没有第二 boss"）→ 先确认对方是否礼让，确认了就**也改共存**，
   并把**破坏性动作收窄**：原来 `foreach (act) if (act != target) 清空第二 boss` 会连别人的安排一起删，
   改成"只清基础游戏误加在**最后一幕**上的那个，且只碰本模组自己的追加幕"；
3. `HasCompetingMod`（仍在用的放权点）日志从"默认关的 DebugLog"升级成 `Logger.Warn` 且每个目标只报一次；
4. 关键功能的**成功路径**也补一条 `Info`（例：`[SynthHearth] 已在第 N 层注入 1 个合成节点（RestSite，…）`）。

> 判据一句话：**别人的 patch 改的是"同一个方法的别的事" ⇒ 共存；改的是"同一个事实" ⇒ 才谈放权，
> 且必须先看对方有没有"已存在就跳过"的礼让。**

---

## 第 9 章　多人联机的红线

1. **版本串必须逐字一致**（见 2.4）
2. **确定性**：任何影响游戏 state 的计算，host/client 必须得到相同结果
   - 不要引入新的随机源；要用 rng 就只用**已同步**的流（如 `Rng.UpFront`）
   - 更稳的做法是**完全确定性的纯函数**（池差集 + 稳定排序 + 字符串散列）
3. **配置同步**：进战斗的数值必须同步（否则 checksum 不匹配 → `State divergence` 踢人）
   - 纯本地表现（如速度倍率、UI 偏好）用 `[ConfigSyncIgnore]` 明确排除
4. **地图/遭遇生成**：`GetNumberOfRooms` 一变，regular encounter 的循环次数就变 →
   消耗的 rng 次数不同 → 紧接着的 elite 抽取整体错位 → **两端打出不同的怪**。
   所以"地图长度"这类会影响生成的配置**必须两端一致**。
5. **不要假设没人测过的路径**：连参考模组 `BossGauntlet` 都写着
   `Multiplayer has not yet been runtime-tested.`

---

## 第 10 章　调试与验证工具箱（本项目沉淀，可直接复用）

| 工具 | 作用 |
|---|---|
| **DumpApi** | 程序集元数据全量转储（含形参名），并报告成员枚举错误数 |
| **DumpIl** | Mono.Cecil 反汇编指定方法，读游戏真实逻辑 |
| **DumpStrings** | 读程序集字符串字面量（找资源路径、事件名、键名） |
| **check-patch-targets** | 抽源码里所有 Harmony 目标 → 对元数据校验存在性 |
| **MakePck** | 手工生成 `.pck`（免装 Godot），含反读自证 |
| **PckParser**（临时脚本） | 反读 pck 头/目录/数据，核对 path/size/MD5 |

### 日志是主要证据来源

游戏日志：`%APPDATA%\SlayTheSpire2\logs\godot.log`

**自己埋点**时，日志要能**直接回答问题**，别只打"出错了"。好例子：

```
[Act5] 填充名单(3): A -> B -> C                    ← 名单内容
[Act5] normalEncounters 写入完成（3 项）: A -> B -> C | rooms.Boss='C'   ← 写入结果
[Act5] 实例校验: __instance=xxx state.Act=yyy 同一实例=False             ← 身份校验（一针见血）
[ActDepth] act4 深度 = 12 rooms (未打精英 12, 多人=False)                ← 输入 + 输出
```

**差例子**：只打 `[Act5] 填充完成`。那对定位毫无帮助。

### 诊断日志要能关掉

我一度留下一个每帧打印的 `[DESYNC-DIAG]` patch，
把玩家日志淹没到无法阅读（用户抱怨"那么吵"）。**诊断代码要么带开关，要么定位完就删。**

**做法（本项目最终采用）**：在配置里加一个 `DebugLogging`（默认 **false**），
所有诊断日志走统一入口：

```csharp
public static void DebugLog(string message)
{
    try
    {
        if (!NotEnoughDifficultyConfig.DebugLogging) return;
        Logger.Info(message);
    }
    catch { /* 日志失败绝不影响游戏 */ }
}
```

**错误与警告仍直接输出、不受开关控制** —— 出问题还是看得到。
排查时让玩家打开这个开关，把 log 发过来即可。

### ⚠️ patch 入口要**无条件**先打一行

这一条是本项目最贵的教训之一。我的合成节点 patch 在日志里**0 次命中**，
而我**无法区分**下面三种情况：

- (a) 目标方法压根没被调用
- (b) 进去了但前置条件 `return` 了
- (c) 进去了但抛异常被 `PatchScope` 吞掉了

因为我在失败路径上不打日志。**修法：patch 入口第一件事就是无条件打一行"我进来了"+ 关键入参**：

```csharp
[HarmonyPostfix]
public static void SomePostfix(NMapScreen __instance, ActMap map)
{
    // 无条件先打：区分"没被调用 / 提前 return / 抛异常"
    Logger.Info($"[Tag] 触发: map={map?.GetType().Name} secondBoss={map?.SecondBossMapPoint == null ? "无" : "有"}");
    ...
    var ok = PatchScope.Run(nameof(SomePostfix), () => { ... });
    Logger.Info($"[Tag] 结果 = {ok}");     // 结尾也打一行，区分"跑完了"与"中途抛了"
}
```

**这一次加日志之后，问题立刻定位**：日志显示 `secondBoss=无` ——
说明地图压根没建第二个 boss 点，根因不在我的注入逻辑，而在上游（见 4.3.2）。

---

## 第 11 章　流程检查清单（写 mod 时按这个走）

**开工**
- [ ] 用官方模板建工程（`dotnet new alchyrsts2mod`）
- [ ] `Directory.Build.props` 里 `GodotPath` / `Sts2Path` 配对
- [ ] 记住：**改非代码文件必须 Publish**，不能只 Build

**写代码**
- [ ] 需要 override 的先查**是不是 virtual**（用 DumpApi）
- [ ] Harmony 形参名从元数据抄，不凭记忆
- [ ] 逐类隔离 patch + 统一 try/catch 包裹
- [ ] 改配置字段时同步加 `.title` / `.description`，两套键名都写（字段派生 + 代码手写）

**改地图 / 遭遇（最危险）**
- [ ] **绝不事后改写已创建的地图** → 用 `CreateMap` override 或"只追加"的合成节点法
- [ ] 自定义 `ActMap` 必须提供 `BossMapPoint` / `StartingMapPoint`（否则 UI 崩）
- [ ] 自定义点必须**放进 `Grid`**（`EnterMapCoordInternal` 会回查 `GetPoint(coord)`，取不到就黑屏，见 12.3-4）
- [ ] 换 `RoomSet` 的池 → **同时归零 `*Visited` 计数器**
- [ ] 改运行时 act → **先校验 `ReferenceEquals(__instance, state.Act)`**

**自己复刻 UI 节点（第 12 章）**
- [ ] 用 `new` 造实例，不用 `scene.Instantiate<T>()`
- [ ] donor 场景**只搬美术、永不入树**（否则原版 `_Ready` 会覆盖你的数据）
- [ ] 自己写 `_Ready` + 自己 `ConnectSignals()`（原版 `_Ready` 会 throw）
- [ ] 基类所有 virtual / abstract 逐个实现；先查有没有 `sealed`（sealed 只能复用不能改）
- [ ] 别照抄依赖**原版 private 状态**的判断（如 `if (!IsFocused) return;`，见 12.3-1）
- [ ] 自定义点先登记 `_points` + `_mapPointDictionary`，再补 `DrawPaths` + `RecalculateTravelability`

**联机**
- [ ] 影响 state 的计算是否确定性？
- [ ] 是否消耗了 rng？能否改成纯函数？
- [ ] 该同步的配置有没有漏？该 `[ConfigSyncIgnore]` 的有没有误同步？

**收尾**
- [ ] clean build 通过（删 bin/obj/.godot）
- [ ] Harmony 目标全部存在
- [ ] 诊断日志已关掉或已删
- [ ] manifest `version` 与 README 同步

---

## 第 12 章　自己复刻一个游戏节点类（"注册新类"标准做法）

> 适用场景：原版某个 UI 节点的行为被**硬编码**了，你要的却是"同一外观、不同数据"。
> 本项目的实例：ACT5 需要三个**各显各得**的 BOSS 地图节点，而原版
> `NBossMapPoint._Ready` 里写着（IL 实证）
> ```csharp
> e = (Point == map.SecondBossMapPoint) ? Act.SecondBossEncounter : Act.BossEncounter;
> ```
> —— 整张地图**只有两个 BOSS 图标槽位**，节点自己没有话语权。
> 解法不是"多摆几个 BossMapPoint"，而是**自己写一个节点类去复刻它**（`Act5BossNode`）。

### 12.1 三条铁律

1. **要复刻的是"类 + 逻辑"，不是"复制粘贴反编译代码"**。美术资源照搬，行为自己实现 ⇒ 数据由你定。
2. **实例必须用 `new` 造**（`new Act5BossNode()`），不要 `scene.Instantiate<T>()` —— 后者的托管类型由
   `.tscn` 根节点挂的脚本决定，你永远拿不到自己的子类。
3. **别调用原版那个 `_Ready`**。原版自己也这么要求：`NMapPoint._Ready()` 第一步就是
   `throw new InvalidOperationException("Don't call base._Ready()! Call ConnectSignals() instead.")`
   ⇒ 子类的规矩是「自己写 `_Ready` + 自己 `ConnectSignals()`」。

### 12.2 复刻步骤（以 `Act5BossNode : NMapPoint` 为例）

```csharp
internal sealed partial class Act5BossNode : NMapPoint
{
    private const string BossSceneKey = "ui/boss_map_point";   // 原版私有常量的字面量，自己写一份

    internal static Act5BossNode Create(MapPoint point, NMapScreen screen, IRunState runState, EncounterModel enc)
    {
        var node = new Act5BossNode          // ① 自己 new
        {
            Name   = $"Act5BossNode_{point.coord.col}_{point.coord.row}",
            Point  = point,                  //    基类 protected setter
            _screen    = screen,             //    基类 protected 字段
            _runState  = runState,
            _encounter = enc                 // ② 数据挂在自己身上（这就是"各显各得"的来源）
        };
        node.GraftSceneVisuals();            // ③ 搬美术
        return node;
    }
}
```

**③ 搬美术子树（关键技巧）**：把原版场景 instantiate 成 **donor，但永不入树** ——
`_Ready` 只在入树时触发，所以原版逻辑一次都不会跑；搬完就 `Free()` donor。

```csharp
var donor = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath(BossSceneKey)).Instantiate();

// ④ 在 donor 身上、搬之前把引用认领好（那时它自己的"唯名表"还完好）
_spriteContainer = Take<Node2D>(donor, "SpriteContainer");     //   Take = GetNodeOrNull("%名") ?? 按节点名递归找
_spineSprite     = Take<Node2D>(donor, "SpineSprite");
_iconImage       = Take<TextureRect>(donor, "PlaceholderImage");
_iconOutline     = Take<TextureRect>(donor, "PlaceholderOutline");
_controllerSelectionReticle = Take<NSelectionReticle>(donor, "SelectionReticle");   // 基类要用，别让它为 null
if (Take<NMultiplayerVoteContainer>(donor, "MapPointVoteContainer") is { } vote) VoteContainer = vote;

if (donor is Control c) { Size = c.Size; CustomMinimumSize = c.CustomMinimumSize;   // ⑤ 布局参数照搬
                          PivotOffset = c.PivotOffset; MouseFilter = c.MouseFilter; FocusMode = c.FocusMode; }

foreach (var child in donor.GetChildren()) { donor.RemoveChild(child); AddChild(child); }   // ⑥ 搬子树
donor.Free();
```

**⑦ 自己接信号**（复刻 `NClickableControl.ConnectSignals`，但不碰它的 private 处理函数）：

```csharp
protected override void ConnectSignals()
{
    Connect(Control.SignalName.FocusEntered, Callable.From(() => SafeFocus(true)));
    Connect(Control.SignalName.FocusExited,  Callable.From(() => SafeFocus(false)));
    Connect(Control.SignalName.MouseEntered, Callable.From(OnMouseEntered));
    Connect(Control.SignalName.MouseExited,  Callable.From(OnMouseExited));
    Connect(NClickableControl.SignalName.MousePressed,  Callable.From<InputEvent>(OnMousePressed));
    Connect(NClickableControl.SignalName.MouseReleased, Callable.From<InputEvent>(OnMouseReleased));
    AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
}
```
> `MousePressed` / `MouseReleased` 是 `NClickableControl._GuiInput` **自己发射**的（不是场景连线）
> ⇒ 鼠标点击这条路完全不依赖 `.tscn` 的 `[connection]`，搬运子树不会丢点击。

**⑧ 把基类所有 virtual / abstract 都实现掉**（这一步决定了"像不像原版"）：

| 成员 | 原版语义 | 你该怎么做 |
|---|---|---|
| `_Ready()` | 接信号 + `Disable()` + 上图标 | 自己写全 |
| `TraveledColor` / `UntravelableColor` / `HoveredColor` / `HoverScale` / `DownScale` | **abstract** | 抄原版常量（`StsColors.pathDotTraveled` / `red` / 1.05 / 1.02） |
| `RefreshColorInstantly()` | spine→shader 参数 `map_color`/`black_layer_color`；贴图→`SelfModulate` | 用自己的字段实现 |
| `OnFocus/OnUnfocus/OnPress` | 只做缩放 tween | 用 `_spriteContainer` + `CreateTween().SetParallel(true)` |
| `OnSelected()` | `State = MapPointState.Traveled` | 抄 |
| 取图标 | `BossNodeSpineResource != null` → spine；否则 `BossNodePath + ".png" / "_outline.png"` | 抄；**图标源用你挂的那个 encounter** |

**⑨ 注册进地图**（节点造好还不算完，必须登记）：

```csharp
container.AddChild(node);                    // NMapScreen._points（Traverse 取私有字段）
pointDict[point.coord] = node;               // NMapScreen._mapPointDictionary
DrawPaths(screen, node, point);              // 私有方法，Traverse/AccessTools 反射调用
RecalculateTravelability(screen);            // 同上（重算可通行，才会 Enable）
```

### 12.3 这一路踩过的 8 个坑（都在本项目实测过）

| # | 坑 | 症状 | 正解 |
|---|---|---|---|
| 1 | 照抄 `if (!IsFocused) return;` | 节点可见、可通行、**点了毫无反应**（连投票日志都没有） | `_isFocused/_isHovered/_isControllerFocused` 都是原版 **private**，只有它自己的 private 悬停函数会维护 ⇒ 你的类永远 false。只用 `enabled + 可见 + 左键` 判定 |
| 2 | 节点放在屏幕外 | 点击被吞（`NMapPoint.OnRelease` 里 `if (!_screen.IsNodeOnScreen(node)) return;`，判据 = 全局 y ∈ (0, 屏幕高)） | 位置要在可视范围内；玩家滚屏后才会 true |
| 3 | 位置用网格公式或 BOSS 节点矩形推 | 节点被甩到地图外/挤成一堆 | BOSS 节点 `Size` 巨大（实测宽 2000+），矩形≠图标。**以"先古之名"节点的中心为 x**，y 在"先古之名→最终BOSS"之间 N 等分 |
| 4 | 自定义 MapPoint 只在网格外用 `AddChildPoint` 串链 | **黑屏**：出发动画播完、房间永远建不出来（`NullReferenceException at RunManager.EnterMapCoord_Patch1`） | `RunManager.EnterMapCoordInternal` 会 `State.Map.GetPoint(coord)` 回查并取 `point.PointType` ⇒ **点必须在 `ActMap.Grid` 里**（阅读栈时注意：原方法体会被 Harmony 挪进 patch 包装帧，别误判成别的 mod） |
| 5 | 点进了网格却留着原版建的普通节点 | 两个节点叠在一起 | `SetMap` 会给每个网格点先建 `NNormalMapPoint` ⇒ 注入时 `QueueFree()` 撤掉再挂自己的 |
| 6 | 读档续玩时按**地图类型**取点 | 注入被跳过 ⇒ 节点和连线全没了（`SetMap: map=SavedActMap`） | 读档重建的是 `SavedActMap`；判据要按**数据**（扫网格找 `PointType`）而不是类型；旧存档缺就现场补（反射拿 `MapPoint[,]` 后直接写元素） |
| 7 | 把可变模型交给 `CreateRoom` | `MutableModelException: Mutable model … used in incorrect place` ⇒ 黑屏 | `RunManager.CreateRoom` 自己会 `PullNextEncounter(...).ToMutable()` ⇒ 你的 `PullNextEncounter` 钩子必须返回**原型**（canonical） |
| 8 | 以为房间类型由 `CreateRoom` 的参数决定 | 拿 BOSS 的 encounter 造出 BOSS 房 → 终局奖励 → 直接进结局 | `CombatRoom.get_RoomType() => Encounter.RoomType`（IL 实证）。房间类型要拦就 patch 这个属性 |

### 12.4 收尾判定：**"只有带标记的 BOSS 房才允许进终局"**

原版 `NRewardsScreen.OnProceedButtonPressed` 的 IL：
```csharp
if (_isTerminal && (CurrentRoom.RoomType == Boss || CurrentRoom.IsVictoryRoom)) {
    if (map.SecondBossMapPoint != null && CurrentMapCoord == map.BossMapPoint.coord)
         ProceedFromTerminalRewardsScreen();                 // ← 双BOSS"中间那一步"：关奖励界面 + 开地图
    else { _proceedButton.Disable(); ActChangeSynchronizer.SetLocalPlayerReady(); }   // ← 结束本幕
}
// SetLocalPlayerReady → VoteToMoveToNextActAction → MoveToNextAct → RunManager.EnterNextAct
```
所以"打完某个 BOSS 就进结局"的**唯一闸门就是这一段**。做法：`[HarmonyPrefix]` 判定
「这场 encounter 是我方伪装BOSS **且** 当前坐标 ≠ `map.BossMapPoint.coord`」⇒ 直接调
`RunManager.Instance.ProceedFromTerminalRewardsScreen()` 并 `return false`（跳过原版）。

> ⚠️ **别走"把 `_isTerminal` 压掉让原版走非终局分支"这条路**：那条分支是
> `SkipLocalRewardsSet() + NOverlayStack.Remove(this)`，而终局奖励界面根本不在同步器栈里 ⇒
> `InvalidOperationException: Tried to skip reward set for player …, but they are not currently viewing any reward set!`
> ⇒ 界面关不掉，表现成"**前进键按了没反应**"。

### 12.5 复刻前必做的 IL 功课（配套工具见第 0 章）

1. `DumpIl <asm> <Type> '*'` —— 拿全量字段/属性/**virtual-sealed 标记** + 所有方法 IL。
   先确认你要 override 的成员**不是 sealed**（`NMapPoint.OnRelease` 就是 sealed：只能复用，不能改）。
2. `DumpIl <asm> <Type> <Method>` —— 只读一个方法，用来确认"谁决定图标/类型/收尾"。
3. `FindRef <asm> <成员名>`（本项目自研）—— 反查**谁调用了它**：定位 act 切换链
   （`EnterAct ← EnterNextAct ← MoveToNextAct ← SetLocalPlayerReady`）就是靠它，
   也是靠它排除了"是别的 mod patch 了 `EnterMapCoord`"的误判（扫了 69 个 dll）。
4. 关键结论写进代码注释（**带 IL 片段**）——下次别人（或下一轮的自己）不会重踩。

---

## 附录：本项目实际踩过的坑一览（按代价排序）

| # | 坑 | 代价 | 教训 |
|---|---|---|---|
| 1 | 事后改写已创建地图（三次） | 三次黑屏，用户等了数轮 | 见 4.5 |
| 2 | 改错 act 实例（`GenerateRooms` 的 `__instance` ≠ `state.Act`） | act5 三场同一个 boss | 见第 5 章 |
| 3 | 换池没归零 `*Visited` 计数器 | 同上 | 见 4.4 |
| 4 | 信了 incremental 构建的"0 错" | 给出**错误结论**："源码已兼容、0 行改动" | clean build 才算数 |
| 5 | 元数据转储中途截断 | 误报 `ModConfig.Save` 不存在 | 工具要报枚举错误数 |
| 6 | 本地化键名两套只写一套 | 配置项显示原始 key | 见 7.2 |
| 7 | 诊断日志每帧刷屏 | 用户日志不可读 | 见第 9 章 |
| 8 | 自己的校验脚本算错偏移/用错 API | 两次假阳性，浪费一整轮 | **先验证工具，再信工具结论** |
| 9 | 硬编码资源路径（背景 slug） | 加载失败 | 改成问游戏要（`ModelDb.Act<T>().MapTopBgPath`） |
| 10 | 硬编码 boss 名单 | 与整幕模组不兼容 | 走 `AllBossEncounters` 池 |
| 11 | 把 `MapPointType` 的图标 / 取怪 / 可进入当一件事 | 三次返工：全 Boss→每场同一个怪；前几个 Monster→小怪图标；末端 Boss→错误图标且不可进入 | 三者解耦，见 4.3.1 |
| 12 | 手工 `AddChildPoint` 连边 | `DrawPaths` 重复键崩溃 | 原版从网格推导相邻关系，别连边，见 4.3.3 |
| 13 | 在失败路径上不打日志 | 功能没生效时**无法诊断**（连"有没有被调用"都不知道） | patch 入口先无条件打一行，见第 10 章 |
| 14 | 诊断日志不设开关 | 刷屏淹没正常提示，玩家抱怨 | 加 `DebugLogging` 配置，见第 10 章 |
| 15 | 自己复刻节点时照抄 `if (!IsFocused) return;` | 节点看着全对却**点不动**，白跑两轮 | private 状态维护不了就别抄，见 12.3-1 |
| 16 | 自定义 MapPoint 不入 `Grid` | 出发即**黑屏**（`EnterMapCoordInternal` 回查不到点） | 见 12.3-4 |
| 17 | 把可变 encounter 交给 `CreateRoom` | `MutableModelException` → 黑屏 | 钩子必须返回原型，见 12.3-7 |
| 18 | 以为房间类型 = `CreateRoom` 的参数 | 伪装BOSS打完直接进结局（建筑师） | `RoomType => Encounter.RoomType`，见 12.3-8 |
| 19 | 压 `_isTerminal` 想让原版走"非终局"分支 | 前进键按了没反应（`SkipLocalRewardsSet` 抛异常） | 直接调 `ProceedFromTerminalRewardsScreen()`，见 12.4 |
| 20 | 读档后按地图**类型**取自定义点 | 续玩时节点/连线全消失（`SavedActMap`） | 判据用数据不用类型，见 12.3-6 |
| 21 | 用 `nameof(属性)` 当 Harmony patch 目标 | **5 个 patch 静默失效**，改了代码游戏里毫无变化，我误判过两次 | 属性写 `get_X`，见 3.5 |
| 22 | 直接给原版节点 `rect.Texture = 我的图` | 日志显示"替换 3 张贴图"，**画面纹丝不动**，又白跑两轮 | 先找该贴图的**唯一赋值点**，见 3.8 |
| 23 | 用"**层**的代表 act"当配色/背景来源 | act1 的两个子变体（Overgrowth / Underdocks）长得一模一样，用户一眼看出 | **同一层可以有多个 act**，要精确到 act，见 3.9 |
| 24 | 把"玩家进度差集"当玩法目标数 | act4 只出 9 个精英，用户："就９个精英有点离谱" | 目标数要来自**内容池**（12 个精英），不是"剩多少"，见 3.9 |
| 25 | 用 `SetUpBackground` 的 prefix 给"背景生成"喂状态 | 战斗背景**永远慢一场**（BOSS 战必错） | 生成发生在**预载**里，比 prefix 早；要问"当前房间"，见 3.10 |
| 26 | 用 `IsTravelable` 当"这个点能不能点" | 调试旅行一开，能点自己脚下的点 ⇒ 投票去同坐标 ⇒ **卡死在地图里** | 只认 `State == Travelable`；再加"同坐标点击"总闸，见 3.11 |
| 27 | 用 act 的 UI 色（近黑）当"这层的颜色" | 暗港 266° 与默认紫 278° 肉眼看不出差别，用户以为"提取失败" | 从**美术**统计招牌色相，见 3.12 |
| 28 | 用 `act is Hive => 2` 之类**类型映射**判"第几层" | 模组的 ACT 2 变体认成 -1 ⇒ **双 boss 开关静默失效**（用户："你硬编码了双重BOSS？"） | 按 `ActsByIndex`/`Acts`/`Index` 判，见 3.13 |
| 29 | "按层配置"的开关配**绝对层号** | 别的模组占了第 4 幕后，玩家的 Act4_* 开关跑去管别人的幕 | 槽位绑**自己的幕**（`ConfigLayerOf`），见 3.14 |
| 30 | 以为"改 act 的 getter"能覆盖所有敌人背景 | **自带背景的模组精英**绕过 act 的 getter（IL 分支），切换对它静默失效（用户："背景切换不兼容其他模组的精英"） | 直接截 `EncounterModel.GetBackgroundAssets`，见 3.15 |
| 31 | "这个敌人属于哪个 act"用池子扫描**先到先得** | 模组精英显示成荣耀（基础 act 排在前面）：用户："走得不是模组自带的,而是荣耀三层" | 先问地图历史（按 act 分组），再按候选排序，见 3.16 |
| 32 | 兼容别的模组时"自己造结果"（复制它的产物） | 对方自绘背景不出现，退化成默认场景（用户第二次："不行,还是NO"） | **改写事实（parentAct），让对方的钩子自己跑**，见 3.17 |
| 33 | 猜对方模组怎么实现 | 连走两条弯路 | 直接 dump 它的 dll：`steamapps/workshop/content/<appid>/<id>/*.dll`，见 3.17 |
| 34 | "聚合容器"被当成候选来源（自引用） | **无限递归 + 日志洪泛 → 游戏崩溃**（一次崩掉刷了 4.2 万行 / 9.3MB 日志） | 候选里排除自己 + 递归硬闸 + 日志去重，见 3.18 |
| 35 | 全局改写 `parentAct` 却不分敌人类别 | 本幕**自己的 BOSS 场景被来源幕顶掉**（用户："为什么ACT 5/6的boss场景没了?"） | 拦截要**按类别收窄**（只精英），见 3.19 |
| 36 | 用"名字在不在名单里"判"这一场算不算 BOSS 房" | 名单边打边变 ⇒ 第 4 个伪装BOSS 被判成 BOSS 房 ⇒ **打完直接进建筑师**（结局） | 判据用**坐标**，名单口径用"进入本幕之前"，见 3.20 |
| 37 | 生成物依赖的"计划"在生成**之后**才算 | 地图按"链位 1、火堆 0"造出来 ⇒ **神话档的火堆全没了**（用户："我神话的火堆怎么没了?"） | 计划必须在生成前定稿（`CustomCreateMap` 里补算），见 3.21 |

### 3.6 ⚠️ 改不动原版行为时，**先去 BaseLib 的 `Abstracts` 里找钩子**（本轮最大教训）

**症状**：想按"敌人来源层"切换 ACT4 的战斗背景。
我先后试了：① 换 `EncounterModel.CreateBackground` 的入参 `parentAct`（无效，自定义背景分支不看入参）；
② 在 `NCombatRoom.SetUpBackground` 之后**反射换掉 `Background` 对象**（日志证明换了、在树里、7 个图层、尺寸也继承了 —— **画面就是不变**，因为原版在更晚的地方又按 `_visuals.Act` 生成了一次）。
两轮都白做。

**正解（一行钩子）**：dump BaseLib 的抽象类，发现官方早就留了口子：
```
CustomActModel      : CustomGenerateBackgroundAssets(Rng)     ← 战斗背景资产
                      CustomBackgroundScenePath               ← 战斗背景场景
                      CustomMapTopBgPath/MidBgPath/BotBgPath  ← 地图三层背景
CustomEncounterModel: CustomEncounterBackground(ActModel parentAct, Rng)
```
于是 `Act4Model` 里 override `CustomGenerateBackgroundAssets`，按"本场来源层"返回
`RunProgress.GetRepresentativeAct(layer).GenerateBackgroundAssets(rng)` 即可 —— **不需要反射、不需要关心时序**。

**方法论（写进肌肉记忆）**：
1. 要改的行为"改不动" ⇒ 先 `DumpIl <BaseLib.dll> BaseLib.Abstracts.Custom<X> '*'` 看 virtual 列表；
2. BaseLib 是**官方认可**的扩展层，它的钩子一定在原版流程的**正确时机**被调用（省掉自己找时机这一整类 bug）；
3. 没有现成钩子才退回"patch 原版方法 / 反射"，并且要预判"原版稍后会不会覆盖我的改动"。

**配套技巧**：把"当前这一场的来源层"这类**跨调用链的数据**放在静态字段里，在 `NCombatRoom.SetUpBackground` 的 **prefix** 写、在 `CustomGenerateBackgroundAssets` 里读 —— 二者在同一次调用链内，时序天然正确。

### 3.7 部署纪律（本轮因它浪费了两轮）

- **构建失败时不要继续部署**：我两次"改了没变化"其实是**编译失败**（着色器字符串里写了 ASCII 引号），
  却把旧 dll 当新结果拷过去、还照旧打日志，导致误判。
- 部署前**核对两件事**：`dotnet build` 是 0 错 + 目标 dll 的 `LastWriteTime` 确实变了；部署后比对哈希。
- 游戏运行时 dll 被占用会 `IOException` ⇒ 让玩家先退游戏，或等待重试。
- 着色器代码写在 C# verbatim 字符串（`@"..."`）里时，**注释里绝对不能出现 ASCII 双引号**（会提前终止字符串）。

### 3.8 ⚠️ 改原版贴图/材质前，先找它的**唯一赋值点**（"我改了但它没变"的第二种成因）

**症状**：ACT4 地图边缘那圈纹路要按层变色。代码日志明明说"替换 3 张贴图"，
玩家截图里纹路**一点没变**。上一版还试过改 `Material` 的 shader 参数 —— 也无效。

**IL 一次给出两个答案**（`DumpIl sts2.dll NMapBg '*'`）：
```
=== [private] NMapBg.OnVisibilityChanged() : Void ===
  ldarg.0 / ldfld _mapTop / ldarg.0 / ldfld _runState / callvirt IRunState::get_Act()
  callvirt ActModel::get_MapTopBg() / callvirt TextureRect::set_Texture(...)
  ... _mapMid ← Act.MapMidBg，_mapBot ← Act.MapBotBg
```
1. 这三张贴图有**唯一赋值点** —— `NMapBg.OnVisibilityChanged()`，而且它挂在 `VisibilityChanged` 信号上
   ⇒ 地图每次显示/隐藏都会**重新按 act 取一遍**，把我直接赋的值覆盖掉（我的调用还发生在它之前，必输）；
2. 三个 rect 的 `Material` 实测**全是 `null`**（先打日志确认，别假设）⇒ "改材质参数"那条路本来就是死的，
   我上一版是在改一个不存在的东西。

**正解**：别跟原版抢"谁写这个属性"，而是**让属性的来源本身变色** ——
patch `ActModel.get_MapTopBg/MidBg/BotBg`（本项目早就因为主题化 patch 了这三个 getter），
把"当前该染什么色相"放进静态状态、并进缓存键；层变了就调一次原版的 `OnVisibilityChanged()`
（`Traverse.Create(bg).Method("OnVisibilityChanged").GetValue()`，私有方法无参可以这样直接调）让它重新取图：

```
SetLayer(层, 层色) → 更新静态状态（色相 = 该层 act.MapTraveledColor 的色相）
                   → 缓存键变化 ⇒ getter 返回新色相的贴图
                   → Traverse 调 OnVisibilityChanged() ⇒ 当帧贴上新图
```

**方法论**：
1. 目标视觉元素**先问"它是谁画上去的"**（`DumpIl <type> '*'` 搜 `set_Texture` / `set_Modulate` / `set_Material` 的调用点），
   找到**唯一赋值点**再决定挂哪一环；
2. 属性/字段的**当前值先打日志**（`Material` 是不是 null、`Texture` 多大）—— 本轮两次弯路都源于"假设它有"；
3. 与原版流程**抢同一个属性**几乎总是输：正确姿势是把差异做进**数据源**（getter / 模型 / 钩子），
   让原版自己把新数据读走；
4. "状态放静态字段 + getter 读状态"的写法天然兼容热重载、多 act 变体，也不用关心时序（配合 3.6 的钩子思路）。

### 3.9 ⚠️ "同一层"不等于"同一个 act"：别用层代表当身份（本轮两个 bug 同源）

**用户一句话点破两件事**：
<i>"为什么 ACT 1 的 2 个子变种层级一样的颜色？"</i> ／
<i>"我的意思是，地图里面分配的精英是确定的，不是随机的！而且就９个精英有点离谱了"</i>

**IL 事实（`ModelDb.ActsByIndex` + 各 act 的 `AllEliteEncounters`）**：
```
层1: Overgrowth(3 精英) + Underdocks(3 精英)   ← 一层两个 act 变体！
层2: Hive(3)      层3: Glory(3)               → 全游戏 12 个精英
```
于是两个 bug 都是"把层当身份"造成的：

| bug | 错误做法 | 现象 | 正确做法 |
|---|---|---|---|
| 两个变体同色 | `GetRepresentativeAct(层)`（层里第一个 act）当配色/背景来源 | Underdocks 的精英被染成 Overgrowth 的颜色，背景也一样 | 按"**谁的池子里有这个 encounter**"反查，精确到 act（`FindActOfEncounter`） |
| 精英只有 9 个 | 目标数 = `全部精英 − 已打过`（进度差集） | 打过 3 个精英的那局，act4 只出 9 场 | 目标数 = **内容池长度**（12）；"打没打过"不是玩法参数 |

**重复精英的成因（值得单独记）**：
```csharp
target = Math.Max(名单.Count, rooms.eliteEncounters.Count);   // ← 池子比名单长
rooms.eliteEncounters.Add(名单[i % 名单.Count]);               // ← 取模循环 ⇒ 池里出现重复项
// 然后原版从池里 rng.NextItem() 抽 ⇒ 同一场精英打两遍
```
**正解不是"抽的时候去重"，而是"根本别抽"**：地图一生成就把名单按"离起点由近到远"逐个**钉**到战斗房上
（`Dictionary<MapCoord, EncounterModel>`），进房时按 **`RunState.CurrentMapCoord`** 查表返回
（IL 实证：`AddVisitedMapCoord(coord)` 先 append，随后才 `CreateRoom → PullNextEncounter`，所以坐标已就位）。
这样：确定的、不重复、读档也对（表按地图重建，坐标是身份）。

**方法论（照这个顺序想）**：
1. 要"确定、不重复"的分配 ⇒ 把**分配**提前到"地图生成时按身份（坐标）落位"，
   运行时只做**查表**；不要指望在随机抽取环节消除随机；
2. 任何"取代表/取第一个"的写法都要追问一句：**这个集合里会不会不止一个？**
   （层里多个 act、池里多个同名、一行多个节点……）；
3. 目标数量从**内容池**推（`AllEliteEncounters.Count`），不要从**玩家进度**推
   —— 后者会让"玩得越多内容越少"，与"做成一个完整的 act"冲突。

### 3.10 ⚠️ "我在 X 的 prefix 里写了状态，为什么下游拿到的是上一场的值？"（背景慢一场）

**症状**：ACT4 战斗背景按敌人来源 act 切换，玩家看到的是**上一场**那个 act 的背景，BOSS 战必错。
我原以为"同一个调用链里 prefix 一定先于方法体"，所以把状态写在 `NCombatRoom.SetUpBackground` 的 prefix 里。

**日志给出的顺序证据**（决定性，别靠推理）：
```
[Act4] 战斗房 (3,1) → 指定精英 'BYRDONIS_ELITE'
[Act4] 战斗背景资产取 'Overgrowth'          ← 资产在这里就生成了（用的是上一场写的状态）
[Act4] 本场来源 = 'BYRDONIS_ELITE' → Overgrowth   ← prefix 才跑到
[Act4] 战斗背景资产取 'Overgrowth'          ← 这一场明明是 Underdocks
[Act4] 本场来源 = 'PHANTASMAL_GARDENERS_ELITE' → 'Underdocks'
```

**原因**：同一个钩子（`EncounterModel.GetBackgroundAssets`）有**两个调用点**：
```
EncounterModel.GetAssetPaths           ← 预载！房间一创建就调，早于 SetUpBackground
EncounterModel.CreateBackground        ← 在 SetUpBackground 里（我盯着的那个）
```
只 patch 一个调用点的"时机"没用 —— 另一个调用点更早。

**正解**：不要给"下游"喂状态，让下游**自己问当前事实**：
```csharp
var state = RunStateAccessor.GetCurrentState();
if (state?.CurrentRoom is CombatRoom room)          // IL: _currentRooms.LastOrDefault()
    act = RunProgress.FindActOfEncounter(room.Encounter?.Id?.Entry);   // 房间一创建就准确
```
`RunManager.CreateRoom` 是**先建房间并入栈、再预载、再建节点**，所以 `CurrentRoom` 在任何生成时机都是本场 ✓。

**方法论**：凡是"生成物"（贴图/资产/节点）依赖的输入，都要问：
**这个输入有几个调用点？最早的那个在哪？** 与其猜时机，不如把输入源换成"永远正确的事实"（当前房间/当前坐标），
静态字段只当兜底。这也顺带解决了读档与热重载。

### 3.11 ⚠️ `IsTravelable` 不是"这个点现在能不能点"（act5 卡死在地图里）

**症状**：玩家在地图上**点自己脚下的点**，投票投出去了（日志：`Player vote changed for 1: ->MapVote (gen: 5 coord: (3, 1))`），
然后卡住出不来 —— 因为 `AddVisitedMapCoord` 对已走过的坐标返回 false，房间不会重建，票却已经投了。

**IL 实证**（`NMapPoint.get_IsTravelable`）：
```csharp
var screen = _screen;
if (screen != null && screen.IsDebugTravelEnabled && !screen.IsTraveling) return true;   // ★ 第一分支
if (!screen.IsTravelEnabled) return false;
return State == MapPointState.Travelable;
```
**开发者旅行开关一开（控制台 `travel`），任何节点都算可通行**，包括自己站着的那格 ⇒ 自研地图节点若照抄这个判定，就等于放了把卡死的钥匙。

**两层修法（都要做）**：
1. **自研节点别用 `IsTravelable`**：只认原始状态机 `State == MapPointState.Travelable`；
2. **加一道总闸**：prefix `NMapScreen.OnMapPointSelectedLocally`，`点击坐标 == RunState.CurrentMapCoord` 就吞掉，
   不投票、不切屏 —— 对**原版节点同样生效**（自研地图里的起点/先古节点也是原版节点）。

**方法论**：判断"能不能交互"时，先确认这个判定里有没有"调试/开发模式"短路；
状态机（`State` 枚举）是唯一可靠口径，`IsXxx` 之类的便利属性往往掺了别的条件。

### 3.12 ⚠️ "提取它的颜色"：别拿 UI 色当美术色（暗港看起来像默认紫）

**症状**：act4 地图纹路按来源 act 变色，暗港（Underdocks）那一场玩家说"提取不到它的颜色，变成默认的紫色"。

**查证（日志证明代码是对的）**：
```
[Stripe] 地图纹路 → 层 1，色相 266°（重挂 3 张底图）
[Act4] 地图染色 ← 'LAGAVULIN_MATRIARCH_BOSS' 属于 Underdocks（层 1） 色 180F24
```
`MapTraveledColor = 180F24` = (24,15,36) ⇒ 色相 **266°**，而 act4 的默认紫是 **278°**：**差 12°，肉眼就是没变**。
四个 act 的 UI 色全是**近黑**：Overgrowth 33° / Underdocks 266° / Hive 33° / Glory 231°（Hive 和 Overgrowth 几乎同色）。

**修法**：颜色从**美术**里统计 —— 取该 act 自己的地图底图，对"够饱和(≥0.18)且不够暗(≥0.18)"的像素按饱和度加权做 5° 直方图，
峰值桶中心就是这一层的招牌色相（按 act 缓存，一局一次）。不写死任何色值，整幕模组的 act 也自动有份。

**方法论**：问游戏要"颜色"之前先问一句 **这个颜色是画给人看的，还是拿来渲染的**？
模型里的 `Color` 属性多半是渲染参数（近黑/低饱和），玩家感知的"主题色"要么在美术里（统计出来），
要么在 UI 皮肤里 —— 用错了不会报错，只会"看起来没生效"。

### 3.13 ⚠️ 判"第几层"绝不能按 act **类型**映射（"你硬编码了双重BOSS？"）

**症状**：玩家装了整幕类模组，用它的 **ACT 2 变体**时"双 boss 不出现"（华语原话：
<i>"你硬编码了双重BOSS？我在模组里面的ACT ２变种没有看到双boss"</i>）。
base game 的 Hive 一切正常 —— 因为代码里真的写死了：

```csharp
public static int GetActIndexFromModel(ActModel? act) => act switch
{
    null => -1, Underdocks => 1, Overgrowth => 1, Hive => 2, Glory => 3,
    Act4Model => 4, Act5Model => 5,
    _ => -1,        // ★ 其它模组的 act 变体全落这里
};
```
`IsDoubleBossEnabled(-1) == false` ⇒ **双 boss 静默失效**。同一个 `-1` 还连带影响
"双 boss 之间的合成火堆""按层难度强化""act5 名单"等所有 `GetActIndex*` 的调用点 ——
一个类型表烂掉一整片功能，而且**不报错**。

**游戏自己的三个权威口径（依次用，全部数据驱动）**：
1. `ModelDb.ActsByIndex` —— 官方"层 → 该层的全部 act 变体"分组。IL 实证它的构造：
   ```csharp
   foreach (var act in Acts) {
       if (act.Index < 0) continue;                    // 负 Index 的 act 不进分组
       while (buckets.Count <= act.Index) buckets.Add(new List<ActModel>());
       buckets[act.Index].Add(act);
   }
   ```
   base game：`Overgrowth.Index=0`、`Underdocks.Index=0`（**同一层两个变体**）、`Hive=1`、`Glory=2`
   ⇒ 整幕模组把变体注册到哪一层，就自动在哪一层；
2. `RunState.Acts` 里的**位置** —— 只往 act 列表 append、不设 `Index` 的模组（含本 mod 的 act4/5，
   它们故意用 `Index = -1` 以免被当成可随机抽的层）用这条；
3. `act.Index` 自己声明的层号。

**配套纪律**：认不出层时**必须打 warn**（老实现是静默 `-1`，这个 bug 因此藏了两轮）；
再加一条一行诊断把"每个 act 判成第几层"打出来：
```
[DoubleBoss] 本局 act 分层判定：1=Overgrowth(Index=0) | 2=SomeModAct2(Index=1) | 3=Glory(Index=2) | 4=Act4Model(Index=-1, ...)
```

**方法论**：任何"把游戏对象映射成枚举/层号"的函数都是**兼容性地雷**——
它一旦用具体类型或具体 id 写死，别的模组加同类对象时就会掉进 `default` 分支，
而且因为返回的是"合法但错误"的值（-1/false/0），**不会有任何异常提示**。
判据要来自"游戏自己的分组/顺序/声明"，并且**default 分支必须留痕**。

### 3.14 ⚠️ 追加式内容的"落位"要三件事一起做（ACT 4 心脏兼容）

**背景**：本模组把自己的两个幕**追加**到 act 列表末尾；别的模组（例如 ACT 4 心脏）用
`CustomActModel(3)` 注册进 `ModelDb.ActsByIndex[3]`。游戏自己的 `ActModel.GetRandomList` IL：
```csharp
foreach (var layer in ModelDb.ActsByIndex)          // 每一层各取一个 act
    acts.Add(pickOne(layer));                       // ← 所以"别人的第 4 幕"天然排在我们前面
```
⇒ 追加式内容的落位**本来就该是"排在最后"**，但只靠"我在 postfix 里 append"是不够的：

| 必须做的事 | 为什么 | 本项目的实现 |
|---|---|---|
| **① 永远最后** | 别的模组的 postfix 可能排在我们之后（Harmony 顺序不由我们定） | `ExpandActListPatch` 先 `RemoveAll(自己)` 再 append（幂等）；run 创建时再 `EnsureOursAreLast()` 纠正一次 |
| **② 配置槽位绑"自己的幕"** | 顺延后绝对层号变了，`Act4_*` 开关会跑去管别人的幕 | `ActLayout.ConfigLayerOf(act, state)`：本模组的幕恒为 4/5，基础三层按绝对层号，别人的额外幕返回 -1（我们不改别人的幕） |
| **③ 检测留痕** | 否则"我的 act 编号怎么变了"永远是谜 | `ActLayout.Detect()` 打 `[ActLayout] 检测到第 4 幕已被 'X' 占用 ⇒ 本模组自动顺延为第 5/6 幕` |

**检测口径（不写死任何具体类型）**：
1. `ModelDb.ActsByIndex[3]` 里有非本模组的 act（声明自己就是第 4 幕）；
2. `ModelDb.Acts` 里有 act 声明 `Index >= 3`；
3. 名字/id 里带 `heart`/`心脏`（模组没声明 Index 时的兜底）。

⚠️ **`IsDefault` 不能用来区分"是不是基础 act"**：暗港（Undercocks/Underdocks）是基础 act，
但它的 `IsDefault = false`（IL 实证：Overgrowth/Hive/Glory 都是 true）—— 拿它当"基础 act 过滤器"
会把暗港误判成别人的模组。

**另一个必须一起改的东西**：凡是"层号 == 某个值"的**魔法判断**都要改成**类型判断**。
本轮修的一处：`if (actIdx == 5) return false;`（排除本模组的传奇幕不夹火堆）——
顺延后它排第 6，层号判断就漏了 ⇒ 改成 `state?.Act is Act5Model`。

**方法论**：做"追加式内容"时，**位置、配置、日志三件事必须一起交付**；
只做了"插进去"而没做"配置跟着走 + 结论可见"，
在单模组环境里永远正确、在模组组合里必然出怪事。

### 3.15 ⚠️ "我改了 act 的钩子，为什么有的敌人背景还是不跟着变？"（自带背景的敌人绕过钩子）

**症状**：ACT4 战斗背景按敌人来源幕切换，官方数据 + 自定义 act 都工作正常，
但**某些模组精英**（用户原话：<i>"背景切换不兼容其他模组的精英"</i>）背景完全不跟着走。

**IL 给出的完整分支**（`EncounterModel.GetBackgroundAssets(ActModel parentAct, Rng rng)`，**private**）：
```csharp
AssertMutable();                          // 只在 mutable 实例上调用
if (_backgroundAssets == null) {          // ← 实例级缓存
    if (HasCustomBackground)
        _backgroundAssets = CreateBackgroundAssetsForCustom(rng);        // ★ 自带背景 → 不问 act
    else
        _backgroundAssets = parentAct.GenerateBackgroundAssets(rng);     // ← 我原本只挂了这条
}
return _backgroundAssets;
```
也就是说：**"改 act 的 getter"只能覆盖"不自带背景"的敌人**。模组精英只要
`HasCustomBackground == true`（自带竞技场/自定义场景的那类），act 的 getter **一次都不会被调用**
⇒ 挂在它上面的 `CustomGenerateBackgroundAssets` 静默失效。
另外 `_backgroundAssets` 是**实例缓存**，谁先算过就定型（预载 `GetAssetPaths` 也会进来），
所以"只改 act 侧 + 赌时机"这条路本身就不稳。

**正解**：截住**真正分叉的那个方法**，而不是它的下游——
```csharp
[HarmonyPatch(typeof(EncounterModel), "GetBackgroundAssets")]   // private 方法用字符串名
static bool Prefix(EncounterModel __instance, ActModel parentAct, Rng rng, ref BackgroundAssets __result)
{
    if (parentAct is not Act4Model) return true;         // 只管自己这一幕
    var source = Act4FightSource.Current();              // 本场来源幕（问当前房间，不赌时序）
    if (source == null) return true;
    __result = source.GenerateBackgroundAssets(rng);     // 直接给结果，**不写缓存** ⇒ 顺带免疫实例缓存
    return false;
}
```
配套：`HasCustomBackground` 是 `protected` ⇒ 用 `Traverse...Property("HasCustomBackground")` 读出来打进日志
（"这个敌人原本会走哪条分支"以前完全是盲区）；再给一个开关
（`Act4_OverrideEncounterBackground`，默认开）让玩家能在"跟随来源幕"与"尊重那个模组的美术"之间选。

**方法论（本次最值钱的一条）**：
"目标行为不生效"时，**别停在"我改的那一环"，要继续往下游找分叉点**。
一个方法里若有 `if (A) 用 X else 用 Y`，而我只改了 Y 那条路上的钩子，
那么所有走 A 的对象都会静默绕过我 —— 这类 bug 的日志特征是"我的代码没被调用"，
所以**在最靠近结果的地方加一行日志/一个 prefix**，比在十个上游加钩子都管用。

### 3.16 ⚠️ "一个 encounter 属于哪个 act"不能靠池子扫描的**先到先得**

**症状升级**：上一条修完"没切换"之后，用户又补了一句：<i>"走得不是模组自带的,而是荣耀三层"</i> ——
不是没切，而是**切成了错的幕**：模组精英显示了荣耀（基础 act）的背景。

**根因**：归属判定原来是"按 `ActsByIndex` 顺序扫描，谁先命中算谁的"。
基础 act 天然排在前面，而模组常常把自家精英**同时**注册进"基础 act 的池子"（让它在原版幕里也能出现）
和"自家 act 的池子" ⇒ 扫描先命中荣耀 ⇒ 判错。

**两层修法（先事实、后启发）**：
1. **优先问游戏自己的记录**：`RunState.MapPointHistory` 是 `List<List<MapPointHistoryEntry>>`，
   **外层就是按 act 分组** ⇒ "这个 encounter 是在哪一幕打的"直接可查，精确、确定、读档也在。
   注意跳过本模组自己的幕（那里装的本来就是复用的别人的敌人）。
   没打过（被跳过的精英）才落到第 2 步。
2. **候选全收集 + 明确排序**：把"池子里挂着它的 act"全部收起来，优先**非默认 act**
   （`IsDefault == false`，即模组 act / 变体 act），并把候选清单打进日志：
   ```
   [RunProgress] 'ACTSFROMTHEPAST-NEMESIS_ELITE' 有 2 个候选 act：Glory(默认) / TheBeyondAct ⇒ 取 'TheBeyondAct'
   ```
   ⚠️ 顺带记住（与 3.14 同源）：`IsDefault` 只能用来认"基础**主** act"——
   暗港（Underdocks）是基础 act 却 `IsDefault = false`，所以它不会被这条挤到后面，这是符合预期的。

**方法论**："某个对象属于哪个分组"这种问题，**优先级应该是：
① 游戏记下的事实（历史/存档）> ② 明确写出排序规则的启发式 > ③ 遍历顺序的巧合**。
用 ③ 时在单模组环境里永远是对的，一旦两个分组同时包含该对象就必错，
而且错得"看起来很正常"（背景是某个真实存在的幕，只是不是那一个）。

### 3.17 ⚠️ 兼容别的模组时：**改写"事实"而不是抢着"造结果"**（ACT4 背景三连修）

**背景**：要让本模组那一幕里，敌人用**它自己那一幕**的战斗背景。
我先后走了三条路，只有第三条对 —— 过程很值钱，记下来：

| 版本 | 做法 | 结果 |
|---|---|---|
| ① | 改 act 的 getter（BaseLib `CustomGenerateBackgroundAssets`） | 本模组/原版敌人 OK；**自带背景的模组敌人**绕过它 |
| ② | 截 `EncounterModel.GetBackgroundAssets`，**自己造**：`__result = source.GenerateBackgroundAssets(rng)` | 看着对，实际错：玩家"还是NO" |
| ③ | **改写 `parentAct`**（prefix 里 `ref ActModel parentAct` 改成来源幕）后 `return true`，让原版和别人的钩子照常跑 | ✅ |

**为什么 ② 是错的** —— 把对方模组的代码 dump 出来才看清（`ActsFromThePast`）：
```
LegacyGenerateBackgroundAssetsPatch : Prefix(ActModel act, ref BackgroundAssets __result)   // act 级
LegacyEncounterBackgroundPatch      : Prefix(EncounterModel, ActModel parentAct, Rng, …)   // ★ 敌人级：认 parentAct
LegacyBackgroundCreatePatch         : Prefix(BackgroundAssets bg, ref NCombatBackground __result)
                                      // 按 **assets 对象引用**查表 → 返回它自绘的 NCombatBackground 子类
```
1. 它的自绘背景只在**敌人级钩子**上挂（`parentAct` 是它的 act 才算），②那条路走的是 act 级，
   而 act 级又被 **BaseLib 的 `CustomActModel` 前缀**先截住 ⇒ 它的钩子一个都没跑；
2. 它的"资产 → 背景节点"是**按对象引用**查表的 ⇒ 我自己 new 出来的资产永远不在表里。

**结论（写进肌肉记忆）**：
- 想让"别人的东西"照它的方式工作，**别复制它的产物，要把它的输入条件改对** ——
  改 `parentAct` 这种"事实类"参数，对方（以及第三方）的所有钩子就都自然生效；
- 看到别人用**对象引用**做查表键（`Dictionary<SomeModel, string>`）时，
  千万别自己构造同类对象去顶替 —— 一定命中不了；
- **能 dump 对方模组的程序集就去 dump**（本机 `steamapps/workshop/content/<appid>/<id>/*.dll`）。
  我前两条弯路都源于"猜对方怎么实现"；第三条是打开它的 dll 五分钟后确定的。

### 3.18 ⚠️ "聚合容器"当候选 = 自引用无限递归 + 日志洪泛 = 崩溃（用户："崩溃了"）

**症状**：一次改动后进战斗直接崩。日志文件 **9.3 MB / 6.8 万行**，其中
**42,629 行**是同一组三行在无限重复：
```
[Act4] 战斗背景资产取 'Act4Model'（层 -1）
[RunProgress] 'SOUL_NEXUS_ELITE' 有 3 个候选 act：Glory(默认) / Act4Model / Act5Model ⇒ 取 'Act4Model'
[RunProgress] 认不出 act 'NOTENOUGHDIFFICULTY-ACT4_MODEL' 属于第几层 …
```

**成因链**（每一环单看都"合理"）：
1. 本模组的 act4 是**聚合 act**：`AllEncounters` = 第三层所有 act 的并集
   ⇒ **任何**第三层精英都能在它池子里被搜到；
2. 我给"来源 act"写的启发式是"**优先非默认 act**"（`IsDefault == false` 优先），
   而聚合 act 恰好是非默认的 ⇒ 它把自己选成了敌人的"来源幕"；
3. 来源幕 = 自己 ⇒ `source.GenerateBackgroundAssets()` 又回到本 act 的
   `CustomGenerateBackgroundAssets` ⇒ **自我递归**；
4. 递归里每层都打 DebugLog ⇒ 日志几秒涨到 9 MB ⇒ 游戏崩。

**三层修法（都保留，纵深防御）**：
```csharp
// ① 候选里排除自己（根因）
foreach (var act in AllActsForScan()) { if (act is Act4Model or Act5Model) continue; ... }
// ② 解析结果与"当前幕"相同 ⇒ 视为没解析出来，不折返
if (act is not (Act4Model or Act5Model) && !ReferenceEquals(act, state.Act)) ...
// ③ hook 自身硬闸：绝不用"自己"当来源（ReferenceEquals(source, this) 直接走 base）
```
另外给所有"可能被预载/每帧调用"的钩子日志加了**去重**（同一 (敌人, 来源幕) 只打一次）。

**方法论**：
1. 任何"从一堆候选里挑一个"的逻辑，**第一个要排除的就是"自己/聚合体"**
   —— 它们天然满足"包含该对象"，会稳定地赢下所有启发式；
2. **递归 + 日志 = 崩溃加速器**：递归本身可能只是卡住，但每层打日志会瞬间写爆磁盘/内存。
   写递归路径上的日志时，加个 key 去重是**保命**而不是"优化"；
3. 崩溃排查第一步永远是**看日志大小**：正常一轮几十 KB，涨到 MB 级 = 洪泛，直接去数重复行，
   比读堆栈快得多（本次 30 秒定位）。

### 3.19 ⚠️ "改写事实"也要**按类别收窄**：全局改写会把本幕自己的东西顶掉

**症状**：上一节那个"改写 `parentAct` 让敌人用自己那一幕的背景"上线后，
玩家立刻发现 <i>"为什么 ACT 5/6 的 BOSS 场景没了?"</i> —— 最终 BOSS 用的是**它老家**（三层的荣耀等）
的场景，而不是本幕（传奇/神话）精心做的 BOSS 场景。

**原因**：改写是**无条件**的（只要在本模组的幕里就换）⇒ 连本幕自己的 BOSS 也被换成了别人的场景。
"背景跟来源幕"对**精英**是本意（精英就是从前三层抽上来的），对**本幕 BOSS** 反而是破坏。

**用户口径**（一句话把边界说清了）：
<i>"检测到处于 ACT 5/4 场景且为精英的时候才会走拦截，否则就放过去"</i>

**修法**：加一道 `IsEliteFight()` 闸，两处都闸（拦截点 + act 侧钩子）：
```csharp
// 判据：当前房间的 Encounter.RoomType（兜底看地图点类型）；判不出来就**不拦截**
if (!Act4FightSource.IsEliteFight()) return true;
```

**方法论**：
1. 任何"全局改写/覆盖"的改动，交付前先问一句 **"这个作用域里还有哪些东西不该被改？"**
   —— 本次漏掉的正是"本幕自己的 BOSS"；
2. **收窄作用域 > 事后打补丁**：闸门写成"只对某类别生效"，比事后写"某某情况不生效"更不容易漏；
3. 判不出来时**放行**（保守）：少切一次背景只是不够花哨，顶掉 BOSS 场景是玩家立刻能看见的破坏。

### 3.20 ⚠️ 用"名字在不在名单里"当房间性质判据 = 名单漂移就出错（"打一个BOSS直接进建筑师"）

**症状**：第 5 幕（伪装 BOSS 连战）里，打完某一个伪装 BOSS **直接进结局**（建筑师）。

**排查链（全部靠日志，不靠猜）**：
1. 先定位"结束本幕"的唯一触发点（Cecil 全库扫 `SetLocalPlayerReady` 的调用者）：
   ```
   NRewardsScreen.OnProceedButtonPressed → ActChangeSynchronizer.SetLocalPlayerReady → MoveToNextAct
   ```
   **全游戏只有这一个调用点** ⇒ 所以一定是"奖励界面的继续键走了终局分支"；
2. 终局分支条件是 `_isTerminal && (CurrentRoom.RoomType == Boss || CurrentRoom.IsVictoryRoom)`
   （`IsVictoryRoom` 的 IL 是"房间是 `EventRoom` 且 canonical event 是 `TheArchitect`" ⇒ 战斗房恒 false）
   ⇒ 只能是 **`RoomType == Boss`**；
3. 我们的 `Act5DisguisedRoomTypePatch` 本来会把伪装 BOSS 房改成 Monster，而它用的是
   `Act5BossDisplay.IsDisguised(encounter)`；日志里那句"Boss → Monster"刷了 96 次、
   但"不结束本幕"只有 **3 次** —— 第 4 场时 `IsDisguised` 已经为假了；
4. 为什么为假：名单是 `BuildPlan` 按"**当前没打过**的 boss"排的，**边打边少**；
   而地图节点上的 encounter 是**开图那一刻**就定死的 ⇒ 两边的名单在第 N 场之后必然错位。

**两层修法**：
```csharp
// ① 判据换成坐标（与名单无关，永不漂移）
public static bool IsAtFinalBossNode(RunState? state)   // 当前坐标 == map.BossMapPoint.coord
//   房间类型：act5 里"不在最终BOSS坐标" ⇒ 一律 RoomType.Monster
//   收尾闸门：act5 里"不在最终BOSS坐标" ⇒ 一律不结束本幕
// ② 名单口径换成"进入本幕之前"的战绩（历史是按 act 分组的，天然可得）
RunProgress.GetDefeatedEncounterIdsBeforeCurrentAct(state)
//   ⇒ 整幕内名单恒定，读档续玩也得到同一份
```

**方法论**：
1. **"这一场算什么"这种性质判断，必须挂在不变的东西上**（坐标/节点身份），
   不能挂在会随进度变化的数据上（"还剩谁没打"）—— 后者在单场景测试里永远正确，
   打几场之后就开始错，而且是"看起来完全正常"的错（房间能进、能打，只是结束时走错分支）；
2. **先找"唯一触发点"再改**：全库扫某方法的调用者（Cecil 十行代码），
   比读十层调用栈猜"是谁结束了我这一幕"快得多；
3. **同一条判据要两处一致**（房间类型 + 收尾闸门）。本次两个 patch 各写一份判据，
   修的时候要一起改，否则会出现"房间是小怪房但结算按 BOSS 收尾"这种半吊子状态。

### 3.21 ⚠️ 生成物依赖的"计划"必须在**生成之前**定稿（"神话的火堆怎么没了"）

**症状**：第 5 幕极限（神话）档，"场数 − 1 个火堆"的设计**一个火堆都没出现**。

**日志一眼看出顺序问题**（这是本条最值钱的地方 —— 顺序错误会留下"两行数字对不上"的指纹）：
```
[Act5LinearMap] 建图: 网格 7x13 | 链位 1（伪装BOSS 1 + 火堆 0） | BOSS(3,12)      ← 建图时名单是空的
[Act5] BOSS 编排（极限）: 伪装=… 10 个 … | 火堆间隔=True                          ← 名单是**建图之后**才算的
[Act5Mid] 注入完成: 链位 10（伪装BOSS 10 + 火堆 0）                                ← 地图里根本没火堆位
```
- `Act5LinearMap` 的构造**读计划**：`bossCount = Act5BossDisplay.DisguisedCount`、
  `hearthCount = HearthsBetween ? bossCount - 1 : 0`，并据此建出网格里的链位；
- 而计划原本是在**黑屏窗口**（`ActBlueprint`）里排的 —— 那次建图比它早 ⇒ 读到空计划 ⇒
  地图只有 1 个链位、0 个火堆；
- 后面 `Act5MidBoss` 的"注入"只能在**已有链位**上做文章（它把 Monster 点换成自研 BOSS 节点、
  保留 RestSite 点）⇒ 没有火堆位就永远补不出火堆。

**修法**：把计划的"定稿"提前到**建图入口**，其它调用点保持幂等：
```csharp
protected override ActMap? CustomCreateMap(RunState runState, bool replaceTreasureWithElites)
{
    if (!Act5BossDisplay.HasPlan)                       // 幂等：谁先到谁定稿
        Act5BossDisplay.BuildPlan(runState, extreme);   // 计划只依赖 seed + 本幕之前的战绩 ⇒ 确定
    return new Act5LinearMap();                         // 现在它读到的数量是对的
}
```

**方法论**：
1. 任何"构造函数/生成器里读全局状态"的设计，都要检查 **那个状态是谁、什么时候写的**；
   写晚一步，生成物就少一块，而且**不会报错**（本次表现成"火堆是 0 个"，而不是异常）；
2. 修法优先"**把生产者提前 + 保持幂等**"，而不是"让生成器事后补救"（事后补救要处理各种残缺地图，
   复杂度高得多）；`if (!HasPlan) Build()` 这种幂等入口可以让多个时机都安全调用；
3. 顺序类 bug 的排查口诀：**去找"两处本该一致的数字"**（本次是"链位 1"对"伪装BOSS 10"）。
   看到数字对不上，先怀疑顺序，而不是先怀疑算法。

### 3.22 ⚠️ 想"接管"一个方法之前，先看它里面还包着多少**演出**（"火堆没有进房转场了"）

**症状**：双 BOSS 之间插入的合成火堆已经能点了，但点下去**没有原版的进房转场**
（画圈 + 渐黑），直接黑一下就进了房；而原版房间（普通怪物/精英）转场正常。

**取证**：去读被我们挂 patch 的那个方法本体 —— 原版 `NMapScreen.TravelToMapCoord`：
```csharp
IsTraveling = true;  RecalculateTravelability();
_marker.HideMapPoint();  IsTravelEnabled = false;
await new MapSplitVoteAnimation(this, _runState, _mapPointDictionary).TryPlay(coord);  // ← 画圈
var node = _mapPointDictionary[coord];
node.OnSelected();  SfxCmd.Play("event:/sfx/ui/map/map_select");
node.AddChildSafely(NMapNodeSelectVfx.Create(scale));                                   // ← 选中特效
SfxCmd.Play("event:/sfx/ui/wipe_map");
var fadeOutTask = TaskHelper.RunSafely(RunManager.Instance.FadeOut());                  // ← 渐黑
foreach (var tick in _paths[(lastVisited, coord)]) { ... 逐点点亮 ... }                  // ← 走线动画
_marker.SetMapPoint(node);  await fadeOutTask;
await RunManager.Instance.EnterMapCoord(coord);                                         // ← 真进房
TaskHelper.RunSafely(RunManager.Instance.FadeIn());
```
我们当初为了"让合成节点进得去房"，在 `TravelToMapCoord` 的 prefix 里 **`return false` + 自己调
`EnterMapPointInternal`** —— 等于把上面**每一行演出**都删掉了。转场不是"另一个系统"，
它就是这个方法本体。

**修法**：把"拦截点"从 `TravelToMapCoord` 挪到**更靠里、且确实必须改写**的那一步。
原版 `RunManager.EnterMapCoord` 的实现是：
```csharp
MapPoint point = State.Map.GetPoint(coord);                       // ← 网格外坐标解析不出来（null）
return EnterMapPointInternal(coord.row + 1, point.PointType, ...);
```
合成坐标是**网格外**的（虚拟行 = boss 行 + 50），`GetPoint` 必然拿不到 ⇒ 这一步**必须**接管；
而它上游的 `TravelToMapCoord` **必须放行**。于是变成：
```csharp
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
[HarmonyPrefix]
public static bool Prefix(MapCoord coord)
{
    // 只留一行证据日志，然后一律放行：演出全在原版实现里
    return true;
}
```

**方法论**：
1. **接管前先通读被接管的方法**：凡是名字里带 `Travel` / `Enter` / `Play` / `Animate` 的方法，
   大概率"逻辑 + 演出"混在一起，整个 `return false` 就是连演出一起删；
2. 拦截点要选**离"必须改的那一行"最近**的位置 —— 越靠外，删掉的原版行为越多。
   本次的正确分层是：**外层的演出放行 / 内层解析不了坐标的那一步接管**；
3. 回归自检口诀：改完一个 `return false`，问自己"**这个方法里除了我要跳过的逻辑，
   还有哪些副作用（信号、音效、动画、存档、UI 刷新）会一起没掉？**"
   —— 本次漏掉的是 `FadeOut/FadeIn`、`_marker`、`VisitedMapCoords`、`RefreshAllPointVisuals`。

---

### 3.23 ⚠️ 同名**重载**不给参数类型 ⇒ `Ambiguous match` ⇒ 整个 patch 类被跳过（2026-09-22 修）

`[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.AfterCreatureAdded))]` 只写了**方法名**，
而这个方法有**两个重载**：

```
TYPE MegaCrit.Sts2.Core.Combat.CombatManager
  METHOD AfterCreatureAdded(Creature creature)                 ret=Task  vis=public   （实例）
  METHOD AfterCreatureAdded(Creature creature, CombatState s)  ret=Task  vis=private static
```

Harmony 在挂 patch 时按 `声明类型 + 方法名` 查，命中两条 ⇒ 抛
`HarmonyException: Ambiguous match for HarmonyMethod[(class=…CombatManager, methodname=AfterCreatureAdded, …)]`。

**为什么"看起来没事"**：本项目是**逐类隔离 patch**（§3.2）——失败的那个类只记一行
`Patch class … failed (skipped)`，其余 patch 照常工作。后果是那个"双重保险"补丁**每天启动都静默失效**，
5 份游戏日志里都躺着同一行 ERROR，但没人注意（因为功能表面正常）。

**修法**（三种，按适用性排序）：
```csharp
// ① 属性里给参数类型（最省事，编译期就能校验 Type 存在）
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.AfterCreatureAdded), new[] { typeof(Creature) })]

// ② 用 AccessTools 自己解析再 ManualPatch
var mi = AccessTools.Method(typeof(CombatManager), "AfterCreatureAdded", new[] { typeof(Creature) });

// ③ 多个重载全都要 patch ⇒ [HarmonyPatch] + [HarmonyTargetMethods]
```

**顺带的教训**：`grep 'failed (skipped)' %APPDATA%\SlayTheSpire2\logs\godot.log` 应该是每次实机回归的**第一条命令**
——它一秒钟就能把"静默失效的 patch"全列出来（本项目当时正好捞到这一条）。

---

## 第 13 章　皮肤 Mod：三条路线、动画契约与资源覆盖

> 本章全部结论来自**反编译 + 实测对照**：`CharacterSkinManager.dll`（奥卡皮肤用的运行时换装器，
> 45 KB / 37 个类型）、创意工坊 `RedMist`（资源覆盖式皮肤）、以及原版 `SlayTheSpire2.pck`（2 GB）。

### 13.1 三条路线（先选路线，再写代码）

| 路线 | 做法 | 优点 | 代价 |
|---|---|---|---|
| **A. 资源覆盖式**（RedMist） | 出一个完整 Godot mod pck，**按原版路径**覆盖 `animations/characters/ironclad/*`、`animations/character_select/…`、`images/…` | 0 运行时风险、不依赖任何游戏 API、不怕版本更新 | ① **同路径撞车**：谁后挂载谁通吃（见 13.2）；② 骨架必须**自带全部动画名**；③ 不能在游戏里切换皮肤 |
| **B. 运行时换装式**（CharacterSkinManager） | `res://skins/**/skin.json` 描述骨架；DLL 在 `NCreature._Ready` postfix 里 `SetSkeletonDataRes` 换骨架 + 重建动画器 | 可切换、多皮肤共存、不动原版资源 | 依赖游戏内部 API ⇒ 版本一变就崩（见 13.5 的 NRE 案例） |
| **C. 改原版模组** | 直接改别人的皮肤包 | 表面最省事 | 没法维护、发布要带别人的资源；**除非你就是作者/维护者** |

**选型建议**：只做一套外观（"把铁甲战士换成 XX"）→ 走 A；
要做"同一角色多套皮肤、游戏内切换"→ 走 B，并且**必须**自己实现动画名兜底（13.6）。

### 13.2 资源覆盖式（A）：pck 里到底要放什么

RedMist 的 pck（50 个条目）结构就是标准答案 —— 注意它**根本没有"换装逻辑"**，
只是把原版路径换个内容：

```
animations/characters/ironclad/ironclad.skel.import      ← 指向 .godot/imported/ironclad.skel-<hash>.spskel
animations/characters/ironclad/ironclad.atlas.import
animations/characters/ironclad/ironclad.png.import
animations/characters/ironclad/ironclad_skel_data.tres.remap
animations/character_select/ironclad/…        （选人界面）
animations/merchant/ironclad/…                （商店）
animations/rest_site/ironclad/…               （篝火）
images/packed/character_select/… , images/ui/top_panel/… , images/ui/hands/…   （卡图/图标/联机手势）
.godot/imported/*.spskel / *.spatlas / *.ctex （真正的资源二进制）
.godot/exported/…/*.res                       （_skel_data.tres 的导出体）
```
其 DLL 只有 **5 KB**，反编译出来只有一个 `GodotPlugins.Game.Main`：
```csharp
[UnmanagedCallersOnly(EntryPoint = "godotsharp_game_main_init")]
private static godot_bool InitializeFromGameProject(...)   // Godot 自举桩，零 patch
```

**⚠️ 路径撞车（实测三家同路径）**：`.godot/imported/<源文件名>-<hash>.<ext>` 里的 **hash 只跟源路径有关，跟内容无关**。
所以只要两个模组都覆盖 `res://animations/characters/ironclad/ironclad.skel`，
它们就会产出**同名文件**：

| 提供者 | `ironclad.skel-8e96930d….spskel` 大小 |
|---|---|
| 原版 `SlayTheSpire2.pck` | 173,712 B |
| 创意工坊 `RedMist` | 75,474 B |
| `奥卡皮肤-Orca` 板甲皮肤 | 482,767 B |

→ 谁先挂载谁被后者覆盖，**外观随机变成另一个模组的模型**（而且不报错）。
做 A 路线时：① 尽量用**独占路径**（如 `animations/Orcaweddingdress/characters/ironclad/…`，
奥卡婚纱就是这么做的，所以它不会和原版/RedMist 撞）；② 或者明确声明与某些模组互斥。

### 13.3 运行时换装式（B）：`skin.json` 的完整 schema

`CharacterSkinManager` 扫 `res://skins/**/skin.json`（递归、大小写不敏感），字段如下（反编译所得）：

```jsonc
{
  "character_id": "IRONCLAD",          // 必填：角色 Id.Entry
  "skin_id": "Orca-weddingdress",      // 必填：皮肤唯一 id
  "display_name": "婚纱",              // 必填：面板显示名
  "battle": {                          // 必填：skeleton_data_path 或 skel+atlas 二选一
    "skeleton_data_path": "res://…/ironclad_skel_data.tres",
    "idle": "idle_loop", "attack": "attack", "cast": "cast",
    "hurt": "hurt", "die": "die", "relaxed": "relaxed_loop"
  },
  "merchant":     { "skeleton_data_path": "…", "animation": "relaxed_loop" },
  "rest":         { "skeleton_data_path": "…", "act0": "…", "act1": "…", "act2": "…" },
  "charselect":   { "skeleton_data_path": "…", "animation": "animation" },
  "preview": {                          // 可选，缺省见括号
    "use_battle_skeleton": true, "animation": "idle_loop",
    "scale_x": 0.25, "scale_y": 0.25, "position_x": 120, "position_y": 150
  }
}
```
- `skeleton_data_path` 缺省时用 `skel` + `atlas` 两个路径，管理器会在
  `user://character_skin_manager/generated/<skin>/<scene>/` **自己生成 `.tres`**；
- 四个场景（battle / merchant / rest / charselect）**都必须能解析出骨架**，否则整个 skin.json 被丢弃；
- `LoadResourceWithFallback` 只会修一种错路径：`/animations/merchant/` → `/merchant/`
  （奥卡婚纱的 merchant 路径多写了一层 `animations/`，正是被它救回来的）。

**⚠️ `battle.idle/attack/cast/hurt/die/relaxed` 是死配置**：v0.0.3 的 DLL 里这些字段
**只被赋值、从不被读取**（`CharacterSkinRuntime.CreateBattleAnimator` 直接
`return model.GenerateAnimator(spine)`，用的是**原版角色的状态机**）。
别指望改 JSON 能让动画对上 —— 动画名契约在骨架里（13.4）。

### 13.4 ⚠️ 动画名契约：骨架必须自带这些名字

原版 `CharacterModel.GenerateAnimator(MegaSprite, Creature)` 建的状态（铁甲战士实测）：

| 名字 | 何时请求 | 备注 |
|---|---|---|
| `idle_loop` | 常驻待机（初始状态） | `AnimState.idleAnim` |
| `low_health_loop` | **HP ≤ 25%** 时 | `IsLowHealth => creature.GetHpPercentRemaining() <= 0.25` |
| `relaxed_loop` | `Relaxed` 触发器（商店/篝火等） | |
| `die` | `Dead` 触发 | |
| `attack` / `cast` / `hurt` | `Attack` / `Cast` / `PowerUp` / `Hit` 触发 | `CharacterModel.AnimationStates` |
| `attack_heavy` 等 | 角色模型自己追加的状态 | 铁甲战士有 |

而 `CreatureAnimator` 找动画的逻辑是**只警告、不切换**：
```csharp
if (!_spineController.HasAnimation(_currentState.Id)) {
    Log.Warn($"could not find '{_currentState.Id}' animation on '{value}'");
    return;                       // ← 不改状态、不播动画：上一段动画早已播完 ⇒ 角色定格
}
```
**实测对照**（裸 ASCII 子串搜索，判"在不在"）：

| 名字 | 原版 | RedMist | 奥卡板甲 | 奥卡婚纱 |
|---|---|---|---|---|
| `idle_loop` / `attack` / `cast` / `hurt` / `relaxed_loop` | 有 | 有 | 有 | 有 |
| `low_health_loop` | 有 | **无** | **无** | **无** |
| `attack_heavy` | 有 | **无** | **无** | **无** |

结论：**除了原版，谁都缺这两档** ⇒ 低血/重击定格是所有换皮 mod 的通病（做 A 路线的也不例外）。

### 13.5 三个真实故障（含取证方法）

**故障 A：开局卡在攻击动作（皮肤一换，动画状态机根本没建起来）**
```
[WARN] could not find 'low_health_loop' animation on 'Visuals'
ERROR: System.NullReferenceException
   at CharacterModel.<>c.<get_IsLowHealth>b__157_0(Creature creature)     ← creature 是 null
   at CharacterModel.GenerateAnimator_Patch2(this, controller, creature)  ← 新版：双参
   at <CrossVersionCompat>GeneratedShims.Shim_GenerateAnimator_0(self, spine)  ← CC 按旧版单参转发
   at CharacterSkinManager.CharacterSkinRuntime.CreateBattleAnimator(...)
```
新版签名是 `GenerateAnimator(MegaSprite controller, Creature creature)`；管理器是按 0.108 的
**单参**重载编译的，调用落到 CrossVersionCompat 生成的 shim，转发时 `creature` 传 null ⇒
`IsLowHealth` 里 `creature.GetHpPercentRemaining()` 空引用 ⇒ 异常抛出 ⇒ **动画器没被赋值** ⇒
角色停在骨架的**第 0 个动画**（这个皮肤里正好是 `attack`），打一张攻击牌后其它代码路径
重新驱动动画才恢复 —— 用户描述"开局卡攻击动作，攻击后正常"，机制完全吻合。

**故障 B：残血静止不动** —— 13.4 的名字缺失 + "只警告不切换"。

**故障 C：外观串台** —— 13.2 的路径撞车。

**故障 D：阵亡结算界面上不是自己选的皮肤**（"死亡动画变成板甲了"）
结算界面 `NGameOverScreen.MoveCreaturesToDifferentLayerAndDisableUi` 按"当前在哪个房间"分三路取外观：
战斗房还在 → 复用 `NCombatRoom.CreatureNodes[].Visuals`（已换过皮）；商店房 → 复用 `NMerchantRoom.PlayerVisuals`；
**其它（战斗房已拆掉，例如战斗中被打死、回结算时房间已销毁）** → `player.Creature.CreateVisuals()`
**现造一份新外观**，再 `SpineAnimation.SetAnimation("die", loop: false)`。
第三路的骨架来自 `CharacterModel.VisualsPath` 场景里烘焙的 ext_resource，也就是**原版路径**
`res://animations/characters/ironclad/ironclad_skel_data.tres` —— 而资源覆盖式皮肤（板甲）正是占用这条路径的，
于是结算界面显示成**那套覆盖皮肤**（没装覆盖皮肤时则退回原版角色）。皮肤管理器只给战斗中的
`NCreature._Ready` / 篝火 / 商店 / 选人界面打了补丁，结算界面没人管 ⇒ 只有它露馅。
**修法**：在该方法后处理新建的那批 `NCreatureVisuals` —— 用 `SpineBody.SetSkeletonDataRes(...)`
套上"当前选中皮肤"的 battle 骨架（选择结果透过反射问皮肤管理器的注册表），再按原版意图播 `die`
（骨架没有 `die` 就退 `idle_loop`）。**注意判据要用 `NCombatRoom.Instance == null`**：
战斗房还在时那批节点是玩家+敌人的混排，不能按玩家顺序乱套。

**取证工具链**（本项目已沉淀，见第 10 章）：
| 目的 | 工具 |
|---|---|
| 看 pck 里有什么、导出单个资源 | `_tools\PckTool`（**Godot 4.4+ 是 PCK v3**：文件表在**包尾**，头里有 `dir_offset`；v1/v2 的解析器会读到"条目 0"） |
| DLL → 可读 C# | `_tools\Decomp`（ILSpy 引擎；大程序集务必 `--list` 先找类型名，再 `--filter=` 只反编译命中类型） |
| 判断骨架里有没有某个动画名 | 裸 ASCII 子串搜索（`[System.Text.Encoding]::ASCII.GetString(bytes).Contains("low_health_loop")`）。**别用正则扫"像单词的串"**：二进制里全是噪声，会把 `xxxcast` 这种也当命中 |
| 故障现场 | `%APPDATA%\SlayTheSpire2\logs\godot.log` 里的 `could not find '<名>' animation` 与模组栈回溯 |

### 13.6 修复范式：动画名兜底（本次交付 `CharacterSkinAnimFix`）

三段式，全部"按名字推导"，不写死任何皮肤：

1. **补 creature 重建动画器**（前缀 + `return false`）：从 `MegaSprite` 节点沿父链找 `NCreature`，
   拿 `creature` 后调**新版双参** `GenerateAnimator(spine, creature)`；找不到就返回 null
   （保持"没有动画器"，但**不再抛异常**）；
2. **动画名兜底**：在 `CreatureAnimator.SetNextState` / `AddNextState` 的 prefix 里，
   若 `state.Id` 在骨架的 `GetAnimationNames()` 里不存在，就换成等价动画
   （`low_health_loop → idle_loop`、`attack_heavy → attack`、`*_loop/*_start/*_end` 去掉后缀…，
   最后退回 `idle_loop`）；
3. **⚠️ 必须"原地改写 `AnimState.Id`"，不能另造一个 AnimState**：状态的转移关系
   （`_nextStates` / `_triggerBranchedStates`，"攻击完回待机"）都挂在**原对象**上，
   换对象就丢了 —— 那正是"攻击动作卡住"的成因。`Id` 是只读自动属性，用反射写它的
   `<Id>k__BackingField`。

**⚠️ 模组加载顺序**：本模组可能排在 `CharacterSkinManager` **之前**初始化，那时它的程序集还没载入，
`AccessTools.TypeByName("CharacterSkinManager.CharacterSkinRuntime")` 返回 null。
做法：挂钩失败先记为"挂起"，在动画请求时**每秒重试一次**，命中后再挂上。

### 13.7 皮肤 mod 检查清单

1. 先定路线（13.1）；A 路线优先**独占路径**，B 路线必做动画名兜底；
2. 骨架里逐个确认：`idle_loop`、`low_health_loop`、`relaxed_loop`、`attack`、`cast`、`hurt`、`die`
   （以及该角色自己追加的，如 `attack_heavy`）——**缺一个就会在那个状态下定格**；
3. `.import` 的 `dest_files` 与 pck 里实际打的 `.godot/imported/*.spskel` 必须一致；
4. 装好后**盯着 `godot.log` 搜 `could not find`**：一条都不能有；
5. 换装类 mod 额外验：开局（idle）、攻击、受击、**低血（≤25%）**、死亡、商店、篝火、选人界面。
