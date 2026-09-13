# 校验所有 Harmony patch 目标（类型 + 成员）在当前程序集元数据中是否存在。
# 数据源：_api\sts2.api.txt / _api\BaseLib-3.4.7.api.txt（由 DumpApi 工具导出）
$ErrorActionPreference = 'Stop'

$src = 'D:\AI\AI专用工作间\_repo\StS2-NotEnoughDifficulty\NotEnoughDifficultyCode'
$apiDir = 'D:\AI\AI专用工作间\_api'

# ---------- 1. 建立 API 索引 ----------
$apiTypes = @{}   # fullName -> @{ Members = [System.Collections.Generic.HashSet[string]] }
$apiIndex = @{}   # simpleName -> [string[]] fullNames
$apiFiles = @{
  'sts2'    = "$apiDir\sts2.api.txt"
  'BaseLib' = "$apiDir\BaseLib-3.4.7.api.txt"
}

foreach ($kv in $apiFiles.GetEnumerator()) {
  $current = $null
  foreach ($line in [System.IO.File]::ReadLines($kv.Value)) {
    if ($line.StartsWith('#')) { continue }
    if ($line.StartsWith('TYPE' + "`t")) {
      $p = $line.Split("`t")
      $full = $p[2]
      $current = $full
      if (-not $apiTypes.ContainsKey($full)) {
        $apiTypes[$full] = [System.Collections.Generic.HashSet[string]]::new()
      }
      $simple = ($full -split '\.')[-1]
      if (-not $apiIndex.ContainsKey($simple)) { $apiIndex[$simple] = @() }
      if ($apiIndex[$simple] -notcontains $full) { $apiIndex[$simple] += $full }
    }
    elseif ($null -ne $current -and $line.StartsWith("`t")) {
      $p = $line.TrimStart("`t").Split("`t")
      if ($p.Length -ge 2) { [void]$apiTypes[$current].Add($p[1]) }
      # 同时把成员名单独入索引，便于跨类型查找（判断"是改名了还是搬走了"）
    }
  }
}
Write-Host "API types indexed: $($apiTypes.Count)  (sts2 + BaseLib)"

# 成员名 -> 拥有该成员的完整类型名集合（用于区分「移除」与「迁移」）
$memberOwners = @{}
foreach ($t in $apiTypes.Keys) {
  foreach ($m in $apiTypes[$t]) {
    $name = ($m -split '\(')[0].Trim()
    if (-not $memberOwners.ContainsKey($name)) { $memberOwners[$name] = @() }
    if ($memberOwners[$name] -notcontains $t) { $memberOwners[$name] += $t }
  }
}

# ---------- 2. 从源码抽取 patch 目标 + using 映射 ----------
$rows = @()
foreach ($f in Get-ChildItem $src -Recurse -Filter *.cs) {
  $text = [System.IO.File]::ReadAllText($f.FullName)
  $usings = @{}
  foreach ($m in [regex]::Matches($text, '(?m)^\s*using\s+([\w\.]+)\s*;')) {
    $ns = $m.Groups[1].Value
    $short = ($ns -split '\.')[-1]
    $usings[$short] = $ns
  }
  foreach ($m in [regex]::Matches($text, '\[HarmonyPatch\(\s*typeof\(([\w\.]+)\)\s*,\s*(?:nameof\(([\w\.]+)\)|"([^"]+)")\s*\)\s*\]')) {
    $rawType = $m.Groups[1].Value
    $member = if ($m.Groups[2].Success) { $m.Groups[2].Value } else { $m.Groups[3].Value }
    if ($rawType.Contains('.')) {
      $parts = $rawType -split '\.'
      $full = ($parts[0..($parts.Length - 2)]) -join '.'
    } else {
      $full = if ($usings.ContainsKey($rawType)) { "$($usings[$rawType]).$rawType" } else { $rawType }
    }
    if ($member) { $member = ($member -split '\.')[-1] }
    $rows += [pscustomobject]@{ File = $f.Name; RawType = $rawType; FullType = $full; Member = $member }
  }
  foreach ($m in [regex]::Matches($text, '\[HarmonyPatch\(\s*typeof\(([\w\.]+)\)\s*\)\s*\]')) {
    $rawType = $m.Groups[1].Value
    if ($rawType.Contains('.')) {
      $parts = $rawType -split '\.'
      $full = ($parts[0..($parts.Length - 2)]) -join '.'
    } else {
      $full = if ($usings.ContainsKey($rawType)) { "$($usings[$rawType]).$rawType" } else { $rawType }
    }
    $rows += [pscustomobject]@{ File = $f.Name; RawType = $rawType; FullType = $full; Member = '' }
  }
}

# ---------- 3. 逐条校验 ----------
$report = foreach ($r in $rows) {
  $typeOk = $apiTypes.ContainsKey($r.FullType)
  $resolvedType = $r.FullType
  if (-not $typeOk -and $apiIndex.ContainsKey($r.RawType)) {
    $cands = $apiIndex[$r.RawType]
    if ($cands.Count -eq 1) { $resolvedType = $cands[0]; $typeOk = $true }
  }
  $memberOk = $null
  $movedTo = @()
  if ($typeOk -and $r.Member) {
    $memberOk = $apiTypes[$resolvedType] | Where-Object { ($_ -split '\(')[0].Trim() -eq $r.Member } | Select-Object -First 1
    if (-not $memberOk -and $memberOwners.ContainsKey($r.Member)) { $movedTo = $memberOwners[$r.Member] }
  }
  [pscustomobject]@{
    File      = $r.File
    Type      = $r.RawType
    FullType  = $resolvedType
    Member    = $r.Member
    TypeFound = $typeOk
    MemberOk  = if ($r.Member) { [bool]$memberOk } else { $null }
    Elsewhere = ($movedTo | Select-Object -First 3) -join ' | '
  }
}

Write-Host "`n===== patch 目标校验结果 ====="
$bad = $report | Where-Object { -not $_.TypeFound -or $_.MemberOk -eq $false }
Write-Host "总条目: $($report.Count)   类型缺失: $(($report | Where-Object {-not $_.TypeFound}).Count)   成员缺失: $(($report | Where-Object {$_.MemberOk -eq $false}).Count)"
if ($bad) {
  Write-Host "`n--- 有问题的条目 ---"
  $bad | Format-Table File, Type, Member, TypeFound, MemberOk, Elsewhere -AutoSize | Out-String -Width 400 | Write-Host
} else {
  Write-Host "全部 patch 目标（类型 + 成员）在当前程序集中均存在。"
}
$report | Export-Csv (Join-Path $apiDir 'patch-target-check.csv') -NoTypeInformation -Encoding UTF8
