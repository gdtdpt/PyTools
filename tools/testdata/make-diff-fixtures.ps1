# Generates the two ~1000-line fixtures used by the text-diff tool smoke test.
# diff-a.txt is written as GBK, diff-b.txt as UTF-8 (no BOM) on purpose:
# both must render identically after TextFileLoader encoding detection.
# Coverage: ultra long single lines, very short lines, missing lines (delete),
# extra lines (insert), inline edits at line head and at line tail.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$outDir = $PSScriptRoot
$left = New-Object System.Collections.Generic.List[string]
$right = New-Object System.Collections.Generic.List[string]

function Add-Both([string]$text) { $script:left.Add($text); $script:right.Add($text) }

$long = 'x' * 3200
$longEditedA = ('data.body = ' + ('payload-' * 400)) + 'END-A'
$longEditedB = ('data.body = ' + ('payload-' * 400)) + 'END-B'

for ($i = 1; $i -le 1000; $i++) {
  $plain = 'setting.{0} = value_{0}' -f $i

  if ($i -eq 1) { Add-Both 'a'; continue }
  if ($i -eq 2) { Add-Both ''; continue }
  if ($i -eq 3) { Add-Both 'tiny'; continue }

  if ($i -eq 250) { Add-Both ('huge.line = ' + $long); continue }
  if ($i -eq 640) { $left.Add($longEditedA); $right.Add($longEditedB); continue }

  if ($i % 120 -eq 0) { $left.Add('legacy.{0} = deprecated_{0}' -f $i); $right.Add($plain); continue }
  if ($i % 130 -eq 0) { $left.Add($plain); $right.Add('new.{0} = added_{0}' -f $i); continue }

  if ($i % 50 -eq 0) { $left.Add($plain); $right.Add(('setting.{0} = value_{1}' -f $i, ($i * 7))); continue }
  if ($i % 70 -eq 0) { $left.Add($plain); $right.Add('x_setting.{0} = value_{0}' -f $i); continue }

  if ($i % 37 -eq 0) { Add-Both ('note.{0} = 中文内容示例_{0}' -f $i); continue }

  Add-Both $plain
}

$gbk = [System.Text.Encoding]::GetEncoding(936)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $outDir 'diff-a.txt'), ($left -join "`r`n"), $gbk)
[System.IO.File]::WriteAllText((Join-Path $outDir 'diff-b.txt'), ($right -join "`r`n"), $utf8NoBom)

Write-Output ("diff-a.txt lines={0} bytes={1} (GBK)" -f $left.Count, (Get-Item (Join-Path $outDir 'diff-a.txt')).Length)
Write-Output ("diff-b.txt lines={0} bytes={1} (UTF-8)" -f $right.Count, (Get-Item (Join-Path $outDir 'diff-b.txt')).Length)

# Small fixtures: all row kinds within the first viewport (delete/insert/head-tail inline),
# used for the no-scroll rendering smoke shot.
$smallL = New-Object System.Collections.Generic.List[string]
$smallR = New-Object System.Collections.Generic.List[string]
$zwsp = [char]0x200B   # 零宽空格：不可见字符差异（竖条标识 + 跳转提示的验收点）
$longMidA = 'long.mid = ' + ('a' * 300) + 'MID-A' + ('b' * 300)   # 超长行差异在中段：水平定位验收点
$longMidB = 'long.mid = ' + ('a' * 300) + 'MID-B' + ('b' * 300)
for ($i = 1; $i -le 30; $i++) {
  $plain = 'row.{0} = v' -f $i
  if ($i -eq 5)  { $smallL.Add('unique-left-line.{0} = only-on-left' -f $i); continue }
  if ($i -eq 8)  { $smallR.Add('unique-right-line.{0} = only-on-right' -f $i); continue }
  if ($i -eq 12) { $smallL.Add($plain); $smallR.Add('row.{0} = v_99' -f $i); continue }
  if ($i -eq 15) { $smallL.Add($plain); $smallR.Add('x_row.{0} = v' -f $i); continue }
  if ($i -eq 18) { $smallL.Add($plain); $smallR.Add(('row.{0} = v{1}x' -f $i, $zwsp)); continue }
  if ($i -eq 21) { $smallL.Add($longMidA); $smallR.Add($longMidB); continue }
  $smallL.Add($plain); $smallR.Add($plain)
}
[System.IO.File]::WriteAllText((Join-Path $outDir 'diff-top-a.txt'), ($smallL -join "`r`n"), $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $outDir 'diff-top-b.txt'), ($smallR -join "`r`n"), $utf8NoBom)
Write-Output ("diff-top-a/b.txt lines={0}" -f $smallL.Count)
