<#
.SYNOPSIS
    配布用パッケージを dist/ に作る。

.DESCRIPTION
    次の 2 つを作る。

      dist/andEmuera-<版>.zip       … APK・導入手順・ライセンス表示 (遊ぶ人向け)
      dist/andEmuera-<版>-src.zip   … ソース一式 (upstream/ は含まない)

    ソース zip を一緒に出すのは、上流 Emuera のライセンス (zlib 相当) が
    「改変した旨を明示する」ことを求めているため。改変の中身は patches/ にある。

    署名鍵は tools/make-keystore.ps1 で先に作っておくこと。パスワードは
    環境変数 ANDEMUERA_KEYSTORE_PASS に入れる (このスクリプトは中身を読まず、
    MSBuild へ変数名のまま渡す)。

.EXAMPLE
    $env:ANDEMUERA_KEYSTORE_PASS = '<鍵のパスワード>'
    .\tools\pack.ps1
#>
[CmdletBinding()]
param(
    # 版。既定では csproj の ApplicationDisplayVersion を使う
    [string]$Version,
    [string]$KeyStore = (Join-Path $env:USERPROFILE '.andemuera\andemuera-release.keystore'),
    [string]$KeyAlias = 'andemuera',
    # ソース zip を作らない
    [switch]$SkipSource
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$csproj  = Join-Path $repo 'src\andEmuera.Android\andEmuera.Android.csproj'
$dist    = Join-Path $repo 'dist'
$passVar = 'ANDEMUERA_KEYSTORE_PASS'

Add-Type -AssemblyName System.IO.Compression.FileSystem

# zip を作る。SourceDir の中身を、その相対パスのまま入れる
# (呼び出し側が 1 段親を渡すので、zip の中は andEmuera-<版>/… で包まれる)。
#
# Compress-Archive も ZipFile.CreateFromDirectory も、Windows PowerShell では
# エントリ名の区切りに \ を使う。Windows の展開ソフトは読めるが、Linux / macOS の
# unzip では「andEmuera-0.1\README.txt」という名前の 1 ファイルになってしまうので、
# エントリを自分で足して / に直す。
function New-Zip {
    param([string]$SourceDir, [string]$Destination)

    if (Test-Path $Destination) { Remove-Item $Destination -Force }

    $root = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($Destination, 'Create')
    try {
        foreach ($file in Get-ChildItem $root -Recurse -File) {
            $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $relative,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

# ---------------------------------------------------------------- 事前確認

if (-not (Test-Path $csproj)) { throw "csproj が見つかりません: $csproj" }

$upstreamLicenses = Join-Path $repo 'upstream\emuera.em\Readme\License'
if (-not (Test-Path $upstreamLicenses)) {
    throw @"
上流のライセンス表示が見つかりません: $upstreamLicenses
先に上流を取得してください:
    git clone https://gitlab.com/EvilMask/emuera.em.git upstream/emuera.em
"@
}

if (-not (Test-Path $KeyStore)) {
    throw @"
署名鍵がありません: $KeyStore
先に作ってください:
    .\tools\make-keystore.ps1
"@
}

if (-not (Get-Item "env:$passVar" -ErrorAction SilentlyContinue)) {
    throw @"
環境変数 $passVar が設定されていません。鍵のパスワードを入れてください:
    `$env:$passVar = '<鍵のパスワード>'
"@
}

[xml]$proj = Get-Content $csproj
$displayVersion = $proj.SelectSingleNode('//PropertyGroup/ApplicationDisplayVersion').InnerText
$versionCode    = $proj.SelectSingleNode('//PropertyGroup/ApplicationVersion').InnerText
$appId          = $proj.SelectSingleNode('//PropertyGroup/ApplicationId').InnerText
if (-not $Version) { $Version = $displayVersion }

Write-Host "andEmuera $Version (versionCode $versionCode)" -ForegroundColor Cyan
Write-Host "署名鍵: $KeyStore (alias: $KeyAlias)"
Write-Host ""

# ---------------------------------------------------------------- ビルド

Write-Host "Release ビルド中…" -ForegroundColor Cyan

# パスワードは env: 参照で渡す。コマンドラインに実物を載せるとプロセス一覧から見える
& dotnet publish $csproj -c Release -f net10.0-android `
    -p:AndroidKeyStore=true `
    "-p:AndroidSigningKeyStore=$KeyStore" `
    "-p:AndroidSigningKeyAlias=$KeyAlias" `
    "-p:AndroidSigningStorePass=env:$passVar" `
    "-p:AndroidSigningKeyPass=env:$passVar" `
    -v minimal
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました (終了コード $LASTEXITCODE)。" }

$publishDir = Join-Path $repo 'src\andEmuera.Android\bin\Release\net10.0-android\publish'
$apk = Get-ChildItem $publishDir -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "署名済み APK が見つかりません: $publishDir" }

Write-Host ""
Write-Host ("APK: {0} ({1:N1} MB)" -f $apk.FullName, ($apk.Length / 1MB))

# 署名がデバッグ鍵に落ちていないか見る。落ちたまま配ると、次の版を上書きインストールできない
#
# 探索元によって返る型が違う (Get-Command は CommandInfo、Get-ChildItem は FileInfo) ので、
# 必ずパス文字列に揃える。FileInfo には .Source が無く、& $null で落ちる
$keytool = $null
$cmd = Get-Command keytool -ErrorAction SilentlyContinue
if ($cmd) { $keytool = $cmd.Source }
if (-not $keytool -and $env:JAVA_HOME) {
    $item = Get-Item (Join-Path $env:JAVA_HOME 'bin\keytool.exe') -ErrorAction SilentlyContinue
    if ($item) { $keytool = $item.FullName }
}
if (-not $keytool) {
    # make-keystore.ps1 と同じ探索先
    foreach ($pattern in @(
        "$env:ProgramFiles\Java\*\bin\keytool.exe",
        "$env:ProgramFiles\Microsoft\jdk-*\bin\keytool.exe",
        "$env:ProgramFiles\Android\jdk\*\bin\keytool.exe",
        "$env:ProgramFiles\Android\openjdk\*\bin\keytool.exe",
        "$env:ProgramFiles\Eclipse Adoptium\*\bin\keytool.exe",
        "$env:LOCALAPPDATA\Android\Sdk\jdk\*\bin\keytool.exe")) {
        $found = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $keytool = $found.FullName; break }
    }
}
if ($keytool) {
    $cert = & $keytool -printcert -jarfile $apk.FullName 2>&1 | Out-String
    if ($cert -match 'CN=Android Debug') {
        throw "デバッグ鍵で署名されています。$KeyStore が使われていません。"
    }
    $fingerprint = ($cert -split "`n" | Where-Object { $_ -match 'SHA256:' } | Select-Object -First 1).Trim()
    Write-Host "署名: $fingerprint"
}
else {
    Write-Host "keytool が無いので署名の確認は省略します。" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 組み立て

# zip の中を <名前>-<版>/ で包むため、1 段親を挟んでフォルダごと固める
$stageRoot = Join-Path $dist '_stage'
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
$stage = Join-Path $stageRoot "andEmuera-$Version"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $apk.FullName (Join-Path $stage "andEmuera-$Version.apk")

# --- ライセンス表示
$licenseDir = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
Copy-Item (Join-Path $upstreamLicenses 'LibWebp.LICENSE.txt') $licenseDir
# Emuera / Emuera.NET (移植元) の全文は licenses/ に置いてある
Copy-Item (Join-Path $repo 'licenses\*') $licenseDir
Copy-Item (Join-Path $repo 'src\andEmuera.Android\Assets\fonts\OFL.txt') $licenseDir

# --- 出典表示 (zlib ライセンスの「改変した旨の明示」はここが本体)
$notice = @'
andEmuera {VERSION}

本ソフトウェアは、以下のソフトウェアを改変・統合して作られた Android 移植版です。
andEmuera の作者はオリジナルの作者ではありません。

    Emuera        (MinorShift 氏)      https://ja.osdn.net/projects/emuera/
    Emuera.EM+EE  (EvilMask 氏ほか)    https://gitlab.com/EvilMask/emuera.em
    Emuera.NET    (VVII 氏ほか)        https://gitlab.com/alnatiyan/EmueraDotNet

    Copyright (C) 2008- MinorShift, 妊）|дﾟ)の中の人, VVII

Emuera / Emuera.NET のライセンス (どちらも zlib ライセンス相当) に従い、
ソースを改変した旨をここに明示します。全文は licenses/ にあります。
上流ソースへの変更は、ソース配布物 andEmuera-{VERSION}-src.zip の patches/ に
差分として入っています。

Emuera.NET からの機能移植は EmueraEX 経由で取り込んでいます。

    https://github.com/BluishCat/EmueraEX

同梱しているもの:

    BIZ UDGothic    Copyright 2022 The BIZ UDGothic Project Authors
                    SIL Open Font License 1.1   licenses/OFL.txt

    SkiaSharp       Copyright (c) Microsoft Corporation
                    MIT License                 licenses/SkiaSharp.LICENSE.txt

    .NET            Copyright (c) .NET Foundation and Contributors
                    MIT License                 licenses/dotnet.LICENSE.txt

    Enums.NET       Copyright (c) Tyler Brinkley
                    MIT License                 licenses/EnumsNET.LICENSE.txt

    libwebp         Copyright (c) Google Inc. (SkiaSharp 経由)
                    BSD 3-Clause                licenses/LibWebp.LICENSE.txt

本ソフトウェアは「現状のまま」で提供され、何らの保証もありません。

このリポジトリの内容は Claude Code (Anthropic) を使って作りました。

    https://github.com/BluishCat/andEmuera
'@
$notice = $notice.Replace('{VERSION}', $Version)
Set-Content -Path (Join-Path $stage 'NOTICE.txt') -Value $notice -Encoding utf8

# --- 導入手順
$readme = @'
andEmuera {VERSION}
Android で era ゲーム (Emuera) を遊ぶためのアプリです。


== 1. アプリを入れる ==

andEmuera-{VERSION}.apk を端末にインストールします。
Google Play を通していないので、「提供元不明のアプリ」の許可を求められます。

    adb install andEmuera-{VERSION}.apk

PC を使わない場合は、APK を端末へ転送してファイルアプリから開いてください。


== 2. ゲームを入れる ==

csv と erb を含むゲームフォルダを、次の場所に置きます。複数入れられます。

    /sdcard/Android/data/{APPID}/files/games/<ゲーム名>/

Android 11 以降、この場所はファイルアプリや USB 接続 (MTP) からは触れません。
PC から adb で送ってください。

    adb push <ゲームフォルダ> /sdcard/Android/data/{APPID}/files/games/

  * tar でまとめて送らないでください。Windows の tar はファイル名を CP932 で
    格納するため、端末上で日本語ファイル名が壊れます。adb push は UTF-8 で扱います。

  * Android は大文字小文字を区別します。画像 (resources) をスクリプトの想定と
    違う大小で置くと、Windows では表示できても端末では出ません。

ゲームが 1 つだけならそのまま起動し、複数あれば一覧から選べます。
遊ぶ途中で別のゲームに切り替えるには、アプリを再起動してください。


== 3. セーブデータを持ち込む ==

PC で遊んでいた sav フォルダをそのまま送れば、続きから遊べます。

    adb push <ゲームフォルダ>/sav /sdcard/Android/data/{APPID}/files/games/<ゲーム名>/

同名ファイルは上書きされます。端末側で遊び進めている場合は先に退避してください。
端末から PC へ戻すときは adb pull です。


== 4. フォント ==

等幅フォント (BIZ UDGothic) をアプリに同梱しているので、そのままで桁揃えは揃います。
ゲームが font/ を同梱していれば、そちらが優先されます。

別のフォントを使いたい場合は、次の場所に ttf / otf を置いてください。
同梱フォントより優先されます。

    /sdcard/Android/data/{APPID}/files/fonts/

半角が全角のちょうど半分になる等幅フォントを選んでください。
BIZ UDPGothic のような "P" 付きは比例フォントなので、選択肢が横に繋がります。
等幅でないフォントで起動した場合は、画面上部に注意書きが出ます。


== 5. 操作 ==

    選択肢                タップ
    拡大                  ピンチ、または「拡大」ボタン。1 本指でパン
    メッセージスキップ    長押し、または「スキップ」ボタン (PC の右クリック相当)
    バックログ            上下フリック。「▼ 最新へ」で復帰
    終了                  バックキーではホームに戻るだけです (誤操作で終わらないため)

画面左上に「処理中」「選択肢をタップ」などの状態が出ます。
タップが通ったかどうかは、波紋の色で分かります。


== 6. できないこと ==

    * 音声 (PLAYSOUND / PLAYBGM) … 未実装です
    * Plugins/*.dll の読み込み    … 未対応です
    * 辞書ポップアップ (Rikaichan) … PC 専用機能のため対象外です


== 7. 出典 ==

本ソフトウェアは Emuera および Emuera.EM+EE の改変版です。
オリジナルの作者ではありません。詳しくは NOTICE.txt と licenses/ を見てください。
'@
$readme = $readme.Replace('{VERSION}', $Version).Replace('{APPID}', $appId)
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding utf8

$binZip = Join-Path $dist "andEmuera-$Version.zip"
New-Zip -SourceDir $stageRoot -Destination $binZip
Remove-Item $stageRoot -Recurse -Force

# ---------------------------------------------------------------- ソース zip

$srcZip = Join-Path $dist "andEmuera-$Version-src.zip"
if (-not $SkipSource) {
    $srcStageRoot = Join-Path $dist '_src'
    if (Test-Path $srcStageRoot) { Remove-Item $srcStageRoot -Recurse -Force }
    $srcStage = Join-Path $srcStageRoot "andEmuera-$Version"
    New-Item -ItemType Directory -Force -Path $srcStage | Out-Null

    # upstream/ は入れない (取得手順は README.md にある)。dist/ も入れない
    foreach ($item in @('src', 'patches', 'docs', 'tools', 'licenses', 'README.md', 'andEmuera.slnx')) {
        Copy-Item (Join-Path $repo $item) -Destination $srcStage -Recurse -Force
    }

    # ビルド生成物は落とす。深い側から消さないと親を消したあとの探索で転ぶ
    Get-ChildItem $srcStage -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'bin', 'obj' } |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    New-Zip -SourceDir $srcStageRoot -Destination $srcZip
    Remove-Item $srcStageRoot -Recurse -Force
}

# ---------------------------------------------------------------- 結果

$zips = @($binZip)
if (-not $SkipSource) { $zips += $srcZip }

$lines = foreach ($z in $zips) {
    $hash = (Get-FileHash $z -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path -Leaf $z)"
}
Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Value $lines -Encoding ascii

Write-Host ""
Write-Host "できました:" -ForegroundColor Green
foreach ($z in $zips) {
    Write-Host ("  {0}  ({1:N1} MB)" -f $z, ((Get-Item $z).Length / 1MB))
}
Write-Host ""
$lines | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "次のリリースでは csproj の ApplicationVersion (今 $versionCode) を +1 してください。" -ForegroundColor Yellow
