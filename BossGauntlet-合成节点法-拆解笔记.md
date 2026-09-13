# BossGauntlet 合成节点法（拆解笔记）

来源：`D:\Steam\steamapps\workshop\content\2868840\3789776799\BossGauntlet.dll`
作者：婉拒菲尔兹　版本：v0.1.3　目标：StS2 public beta v0.111.0
（本笔记由 IL 反汇编得出，非源码；dll 已备份到 `_refs\BossGauntlet.dll`）

---

## 一、它解决的问题

在原版地图上"两个 boss 之间插入火堆/商店"。作者的做法**不是改造地图**，而是：

> **保留原版地图一个字节不动，在「地图外的虚拟坐标」新建合成节点，自己画、自己接管可通行性、自己接管进入。**

这与我先前"事后改写 `map.Grid`"的思路完全相反，也是我连续失败的原因。

---

## 二、关键私有字段名（照搬必需）

`NMapScreen` 上的私有字段（用 `HarmonyLib.Traverse` 取，不用反射 FieldInfo）：

| 字段 | 类型 | 用途 |
|---|---|---|
| `_bossPointNode` | `NBossMapPoint` | 第一个 boss 的视觉节点 |
| `_secondBossPointNode` | `NBossMapPoint` | 第二个 boss 的视觉节点 |
| `_points` | `Control` | 地图节点的容器（子节点挂这里） |
| `_mapPointDictionary` | `Dictionary<MapCoord, NMapPoint>` | 坐标 → 视觉节点 |
| `_paths` | `Dictionary<(MapCoord,MapCoord), IReadOnlyList<TextureRect>>` | 连线缓存 |

---

## 三、合成坐标

```csharp
// 恒定偏移 50，越出正常地图范围（看起来"在地图外"，但游戏不管，因为是自己画的）
static MapCoord GetSyntheticCoord(ActMap map, int index)
{
    if (map.SecondBossMapPoint == null)
        throw new InvalidOperationException("Cannot create an inter-boss coordinate without a second boss.");

    int col = map.BossMapPoint.coord.col            + 50 + index;
    int row = map.SecondBossMapPoint.coord.row      + 50 + index;
    return new MapCoord(col, row);
}

static MapPoint CreateSyntheticPoint(ActMap map, int index, MapPointType type)
{
    var c = GetSyntheticCoord(map, index);
    var p = new MapPoint(c.col, c.row);   // 注意：ctor 的 col/row 会被忽略
    p.PointType = type;
    p.CanBeModified = false;
    return p;
}
```

---

## 四、第二个 boss 怎么定（`EnsureSecondBossForAct`）

```
1. runState 为 null → 返回
2. !IsExtraBossEnabled(runState, actIndex) → 返回
3. actIndex >= runState.Acts.Count → 返回
4. act = runState.Acts[actIndex]
5. act.HasSecondBoss 已经为 true → 返回（不覆盖别人安排的）
6. 候选 = act.AllBossEncounters.Where(不是第一个 boss).OrderBy(Id.Entry, StringComparer.Ordinal)
7. 候选为空 → Log.Warn("No second boss candidate was available for {act.Id} at act index {actIndex}") → 返回
8. 索引 = GetStableBossIndex(runState, act, actIndex, 候选数)
9. act.SetSecondBossEncounter(候选[索引])
```

**确定性选择**（`GetStableBossIndex`）：

```
key = $"{runState.Rng.StringSeed}|{act.Id}|{act.BossEncounter.Id}|..."   // 再接 actIndex / 候选数
index = 某个把 key 散列成 [0, candidateCount) 的稳定函数
```

要点：**用 `RunRngSet.StringSeed` 拼字符串做散列，绝不调用 `rng.NextItem(...)`** ——
不消耗任何 rng 流，所以 host/client 各自算都一致，且不会打乱后续随机序列。
（README 原话："Extra-boss selection is deterministic and does not advance any of the game's RNG streams."）

---

## 五、视觉注入（`NMapScreen.SetMap` postfix → `AddInterBossMapVisualsIfNeeded`）

```
1. 取 runState / actIndex；不是启用层 / 没有 SecondBossMapPoint → 返回
2. roomTypes = GetInterBossRoomTypes(runState, actIndex)   // 例如 [RestSite] 或 [RestSite, Shop]
3. 用 Traverse 拿 _bossPointNode / _secondBossPointNode / _points / _mapPointDictionary / _paths
   取不到 → Log.Error("Could not access map nodes required for inter-boss room visuals.") → 返回
4. RemovePath( (BossMapPoint.coord, SecondBossMapPoint.coord), _paths )
   ← 先删掉两个 boss 之间那条直连，否则视觉上会有一条穿过去的线
5. 计算两端位置：
     start = bossNode.Position + bossNode.Size * 0.5f
     end   = secondBossNode.Position + secondBossNode.Size * 0.5f
6. 遍历 roomTypes（i = 0..n-1）：
     pt   = CreateSyntheticPoint(map, i, roomTypes[i])
     node = NNormalMapPoint.Create(pt, mapScreen, runState)      // ← 关键：复用原版节点工厂
     node 挂到 _points 容器（AddChild）
     node.Position = Vector2.Lerp(start, end, (i+1)/(n+1)) - node.PivotOffset
     node.Scale = Vector2.One * 某缩放
     _mapPointDictionary[pt.coord] = node                        // ← 用索引器赋值，不是 Add
     把 pt 收进 List<MapPoint>
     把 node 收进 List<NNormalMapPoint>
7. 重连：
     bossPoint.AddChildPoint(每个合成点)               // 第一个 boss → 合成点
     合成点之间依次 AddChildPoint                       // 合成点 → 合成点
     最后一个合成点 → SecondBossMapPoint
8. 画连线：调用 NMapScreen.DrawPaths(节点, 对应 MapPoint) 逐条画（日志串 "DrawPaths"）
     失败时 Log.Error("Failed to draw inter-boss room chain: ...")
9. SetSyntheticFocusNeighbors(firstBoss, secondBoss, nodes)   // 处理键盘/手柄焦点邻居
```

### 从中学到的三条硬规则
1. **`NNormalMapPoint.Create(MapPoint, NMapScreen, IRunState)` 是公开静态工厂** —— 不需要自己造视觉节点。
2. **`_mapPointDictionary[coord] = node` 必须用索引器**，用 `Add` 撞键就抛（`DrawPaths` 里那个 `Add` 就是官方自己的隐患，见下）。
3. **必须先 `RemovePath` 删掉原来的直连**，否则两条线重叠。

---

## 六、可通行性（`NMapScreen.RecalculateTravelability` postfix）

`RefreshTravelabilityIfNeeded` 用 `Traverse` 取 `_secondBossPointNode` 与 `_mapPointDictionary`，
在游戏算完可通行性之后**再补一遍**，让合成节点变成可点。

---

## 七、进入合成节点（`RunManager.EnterMapCoord` prefix）

`TryEnterInterBossRoom(runManager, coord, out Task result)` 返回 true 表示"我接管了"，
由它自己构造火堆/商店房间并推进流程，**不走原版的 `EnterMapCoordInternal`**。

配套的还有：
- `RunManagerProceedFromTerminalRewardsScreenPrefix` —— 第一个 boss 打完的"继续"按钮
- `RunManagerLoadIntoLatestMapCoordPrefix` —— 读档时若停在合成节点上要恢复
- `MapSelectionSynchronizerPlayerVotedForMapCoordPrefix` —— 多人投票坐标重映射
- `NRestSiteRoomOnProceedButtonReleasedPrefix` / `NMerchantRoomHideScreenPrefix` —— 离开合成房间时收尾

**这说明"往地图里插节点"远不止画出来**：进入、离开、读档、多人投票四个流程都要接。

---

## 八、它明确不碰的东西（README 自述）

- **不 patch 房间生成**、**不 patch act 选择**
- 只在"当前 act 的地图即将生成时"赋值第二个 boss（`RunManager.GenerateMap` prefix）
- 合成节点用固定坐标偏移，`CanBeModified = false`
- README 警告：
  - `Custom map replacements that omit SecondBossMapPoint are not supported`
    ← **对我们在 act5 做的自定义 ActMap 是直接警告**：必须提供 `SecondBossMapPoint`
  - 不要和 AscensionPlus 同时开（它的 Act2 A20 火堆 patch 打同一个 transition，会产生重复合成节点）
  - 多人未做运行期测试

---

## 九、可迁移到本模组的结论

1. **双 boss 的火堆别再改原版节点** → 改成合成节点法（本文第三、五、七节）。
2. **第二个 boss 的抽取要换成"该 act 的 `AllBossEncounters`"**，这样**别的模组注册进该 act 的 boss 自动成为候选**（正是用户要的"走池子、兼容其他模组"）。
3. **选择要确定性且不吃 rng** → 用 `Rng.StringSeed` 拼 key 散列，不要 `NextItem`。
4. **`DrawPaths` 的 `Dictionary.Add` 是官方隐患** → 本模组已用 transpiler 改成索引器赋值（见 `DrawPathsIdempotentAddPatch`），这与 BossGauntlet 用索引器写 `_mapPointDictionary` 是同一个教训。

---

## 十、本模组的落地实现（对照）

已按本文手法写出 `build\NotEnoughDifficultyCode\ExtraActs\Patches\SyntheticHearthPatch.cs`。

### 10.1 字段名**实测核对通过**

我用 `Traverse` 取的 5 个字段，在 `sts2.api.txt` 里逐个核对存在，且与 BossGauntlet 的 IL 完全一致：

```
FIELD  Control _points
FIELD  Dictionary`2 _mapPointDictionary
FIELD  Dictionary`2 _paths
FIELD  NBossMapPoint _bossPointNode
FIELD  NBossMapPoint _secondBossPointNode
```

（另有同名的 `StringName` 静态字段，是 Godot 源生成器产出的属性名常量，不要拿错。）

### 10.2 我在进入侧做了**简化**（与 BossGauntlet 不同）

BossGauntlet 为进入合成房间写了一整套 async 接管
（`MoveToInterBossRoomAsync` / `FinishInterBossTransitionAsync` / `RejectSyntheticTravelAsync` …）。

我改用**游戏公开方法**：

```csharp
// RunManager.EnterMapCoord 的 prefix：
__result = __instance.EnterMapPointInternal(actFloor, MapPointType.RestSite, null, true);
return false;   // 跳过原版
```

`EnterMapPointInternal(Int32 actFloor, MapPointType pointType, AbstractRoom preFinishedRoom, Boolean saveGame)`
会按 `MapPointType` 造对应房间（`RestSite` → 真火堆），所以：
- **不必复制那套 async 流程**
- 走的是**原版房间流程**（奖励/存档/联机都正常）
- 代价：没做 BossGauntlet 那套"debug travel 越级点击拒绝"的加固

### 10.3 仍未实机验证

- 合成节点的**视觉位置**是否落在两个 boss 之间（靠 `Vector2.Lerp` + `PivotOffset` 修正）
- `RecalculateTravelability` 之后合成节点**是否可点**（我保留了该 patch 作挂钩点，但没做额外处理——
  因为连通关系已在 `InjectVisuals` 里 `AddChildPoint` 接好，原版重算时应当能认到）
- 多人下 host/client 是否一致

### 10.4 ⚠️ 一个必须记住的结论

**`map.GetAllMapPoints()` 枚举的是 `Grid`，而 `BossMapPoint` / `SecondBossMapPoint` 不在 `Grid` 里。**

这解释了我最初那个"找第二个 boss 的父节点"为什么必然失败
（日志：`act3 找不到第二个 boss 的父节点，火堆未插入`），
也是"必须用合成节点、而不能改既有节点"的根本原因。