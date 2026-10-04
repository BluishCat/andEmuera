<#
.SYNOPSIS
    PC から Android 端末へ andEmuera とゲーム本体を入れる。

.DESCRIPTION
    USB デバッグを有効にした端末を 1 台つないで実行する。やることは次のとおり。

      1. アプリを入れる (-Apk または -Build を付けたときだけ)
      2. ゲームフォルダを /sdcard/Android/data/<appId>/files/games/<フォルダ名>/ へ送る
      3. フォントを files/fonts/ へ送る (-Font を付けたときだけ)

    ゲームが端末にまだ無ければフォルダごと送る。すでにあれば、両側の
    (相対パス・サイズ・更新時刻) を突き合わせて、新しいものと変わったものだけを送る。
    時刻は PC 側のほうが新しいときだけ差とみなす (以前 cp で入れたものは端末側の時刻が
    入れた日時に変わっているため)。端末にだけあるファイルは消さない
    (端末で作られたものかもしれないため)。

    adb の罠をいくつか避けている。

      * remote 側のディレクトリ名が日本語だと adb push がハングする
        → ASCII の一時名へ送ってから adb shell で mv / cp する
      * 複数ファイルを 1 回の push でディレクトリへ送ると、成功と表示されて何も書かれない
        → 差分はローカルの一時フォルダに構造ごと複製し、ディレクトリ 1 個として送る
      * adb push --sync は全ファイルを stat するので、HDD 上の 16 万ファイルで 1 時間以上かかる
        → 使わない。一覧は .NET で取る (FindFirstFile がサイズと時刻を一緒に返すので速い)
      * tar は日本語ファイル名を CP932 で格納して壊す → 使わない

    セーブ (sav/) は、端末に同じゲームがすでにあるときは送らない。
    端末で遊び進めたセーブを PC の古いセーブで上書きしないためである。
    -WithSave を付けたときだけ、端末の sav/ を PC へ控えてから送る。

.EXAMPLE
    .\tools\deploy.ps1 -Game D:\egame\era\erablue_resort

.EXAMPLE
    .\tools\deploy.ps1 -Build -Game D:\egame\era\erablue_resort -Launch

.EXAMPLE
    .\tools\deploy.ps1 -Apk dist\andEmuera-0.8\andEmuera-0.8.apk

.EXAMPLE
    .\tools\deploy.ps1 -Game D:\egame\era\eraTW -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # 送るゲームフォルダ (csv と erb を含むもの)。複数指定できる
    [string[]]$Game,

    # 入れる APK。中にアセンブリを持たない Fast Deployment の APK は拒否する
    [string]$Apk,

    # dotnet build -c Debug -t:Install でアプリを入れる (開発用)
    [switch]$Build,

    # 端末にすでにあるゲームにも sav/ を送る。端末の sav/ は先に PC へ控える
    [switch]$WithSave,

    # 送る ttf / otf (全ゲーム共通の fonts/ へ)
    [string[]]$Font,

    # 終わったらアプリを起動する
    [switch]$Launch,

    # 端末が複数つながっているときのシリアル (adb devices で見えるもの)
    [string]$Serial,

    # adb.exe の場所。既定では PATH と Android SDK を探す
    [string]$Adb,

    # 端末の sav/ を控える場所
    [string]$BackupDir = (Join-Path $env:USERPROFILE '.andemuera\sav-backup')
)

$ErrorActionPreference = 'Stop'

$repo   = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo 'src\andEmuera.Android\andEmuera.Android.csproj'

# csproj から取れればそれを使う (配布 zip に入れた場合など、無くても動くように)
$appId = 'rip.eragames.andemuera'
if (Test-Path $csproj) {
    [xml]$proj = Get-Content $csproj -Encoding utf8
    $node = $proj.SelectSingleNode('//PropertyGroup/ApplicationId')
    if ($node) { $appId = $node.InnerText }
}

$remoteFiles = "/sdcard/Android/data/$appId/files"
$remoteGames = "$remoteFiles/games"

# adb shell の出力は UTF-8。既定 (CP932) のままだと日本語のパスが化けて突き合わせが外れる
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$utf8NoBom = [Text.UTF8Encoding]::new($false)

# ---------------------------------------------------------------- adb

function Find-Adb {
    if ($Adb) {
        if (-not (Test-Path $Adb)) { throw "adb が見つかりません: $Adb" }
        return (Resolve-Path $Adb).Path
    }
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $sdks = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT,
              "$env:LOCALAPPDATA\Android\Sdk",
              "$env:ProgramFiles\Android\android-sdk",
              "${env:ProgramFiles(x86)}\Android\android-sdk") | Where-Object { $_ }
    foreach ($sdk in $sdks) {
        $p = Join-Path $sdk 'platform-tools\adb.exe'
        if (Test-Path $p) { return $p }
    }
    throw @"
adb が見つかりません。Android SDK Platform-Tools を入れて PATH に通すか、-Adb で場所を指定してください。
    https://developer.android.com/tools/releases/platform-tools
"@
}

$adbExe = Find-Adb

# adb を呼ぶ。-s は常に付ける (途中で 2 台目が刺さっても取り違えないように)
# 引数に -p などが混ざるので、advanced function にせず $args をそのまま渡す
function Invoke-Adb {
    # Windows PowerShell では stderr を 2>&1 で拾うと ErrorRecord になり、Stop だとそこで止まる
    $ErrorActionPreference = 'Continue'
    $out = & $adbExe -s $script:Serial @args 2>&1
    $code = $LASTEXITCODE
    $text = ($out | ForEach-Object { "$_" }) -join "`n"
    if ($code -ne 0) {
        # 一覧を取るコマンドだと出力が数万行あるので、末尾だけ見せる
        $tail = ($text -split "`n" | Select-Object -Last 20) -join "`n"
        throw "adb $($args -join ' ') が失敗しました (終了コード $code)`n$tail"
    }
    return $text
}

# 進捗をそのまま見せたい push 用。adb は進捗や結果を stderr に出すことがあり、
# 呼び出し側が 2>&1 を付けていると Stop で止まるので、ここでも Continue にする
function Invoke-AdbPush {
    param([string]$From, [string]$To)
    $ErrorActionPreference = 'Continue'
    & $adbExe -s $script:Serial push $From $To
    if ($LASTEXITCODE -ne 0) { throw "adb push に失敗しました (終了コード $LASTEXITCODE)。" }
}

# 端末上でシェルスクリプトを走らせる。
# 日本語のパスをコマンドラインに載せると、PowerShell → adb.exe → 端末の sh の
# どこかで引用や文字コードが崩れうるので、UTF-8 のファイルにして送ってから sh で読ませる
$remoteScript = '/data/local/tmp/andemuera-deploy.sh'
function Invoke-Remote {
    param([string]$Script)
    $local = Join-Path $env:TEMP 'andemuera-deploy.sh'
    [IO.File]::WriteAllText($local, $Script.Replace("`r`n", "`n"), $utf8NoBom)
    Invoke-Adb push $local $remoteScript | Out-Null
    Remove-Item $local -Force -WhatIf:$false
    return Invoke-Adb shell "sh $remoteScript"
}

# sh の単一引用符で包む
function Quote-Sh([string]$s) { "'" + $s.Replace("'", "'\''") + "'" }

# ---------------------------------------------------------------- 端末を選ぶ

$devices = @(& $adbExe devices | Select-Object -Skip 1 |
    Where-Object { $_ -match '^(\S+)\s+(\S+)' } |
    ForEach-Object { [pscustomobject]@{ Serial = $Matches[1]; State = $Matches[2] } })

if ($Serial) {
    $dev = $devices | Where-Object Serial -eq $Serial
    if (-not $dev) { throw "端末 $Serial がつながっていません。" }
}
else {
    $ready = @($devices | Where-Object State -eq 'device')
    if ($ready.Count -eq 0) {
        $hint = ''
        if ($devices | Where-Object State -eq 'unauthorized') {
            $hint = "`n端末の画面に「USB デバッグを許可しますか?」が出ていれば許可してください。"
        }
        throw "端末が見つかりません。USB デバッグを有効にしてつないでください。$hint"
    }
    if ($ready.Count -gt 1) {
        throw "端末が複数つながっています。-Serial で選んでください:`n" +
              (($ready | ForEach-Object { "    $($_.Serial)" }) -join "`n")
    }
    $dev = $ready[0]
}
if ($dev.State -ne 'device') { throw "端末 $($dev.Serial) は使えない状態です: $($dev.State)" }
$Serial = $dev.Serial

$model = (Invoke-Adb shell getprop ro.product.model).Trim()
Write-Host "端末: $model ($Serial)" -ForegroundColor Cyan

function Get-InstalledInfo {
    $dump = Invoke-Adb shell dumpsys package $appId
    if ($dump -notmatch 'versionCode=') { return $null }
    [pscustomobject]@{
        VersionName = if ($dump -match 'versionName=(\S+)') { $Matches[1] } else { '?' }
        VersionCode = if ($dump -match 'versionCode=(\d+)') { $Matches[1] } else { '?' }
        Debuggable  = $dump -match 'pkgFlags=\[[^\]]*DEBUGGABLE'
    }
}

$installed = Get-InstalledInfo
if ($installed) {
    $kind = if ($installed.Debuggable) { 'Debug' } else { 'Release' }
    Write-Host "アプリ: $($installed.VersionName) (versionCode $($installed.VersionCode), $kind)"
}
else {
    Write-Host "アプリ: 未インストール" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 1. アプリ

if ($Build -and $Apk) { throw "-Build と -Apk は同時に指定できません。" }

if ($Build) {
    if (-not (Test-Path $csproj)) { throw "csproj が見つかりません: $csproj" }
    if ($installed -and -not $installed.Debuggable) {
        # Debug 鍵で上書きしようとすると署名が合わず失敗する。アンインストールを促すとデータが消えるので止める
        throw @"
端末のアプリは Release 署名です。-Build (Debug 署名) では上書きできません。
アンインストールすると files/ ごとゲームとセーブが消えます。-Apk で Release の APK を入れてください。
"@
    }
    Write-Host ""
    Write-Host "ビルドして端末へ入れます (dotnet build -c Debug -t:Install)…" -ForegroundColor Cyan
    if ($PSCmdlet.ShouldProcess($Serial, 'dotnet build -t:Install')) {
        & dotnet build $csproj -c Debug -t:Install "-p:AdbTarget=-s $Serial" -v minimal
        if ($LASTEXITCODE -ne 0) { throw "ビルドまたはインストールに失敗しました (終了コード $LASTEXITCODE)。" }
        $installed = Get-InstalledInfo
    }
}
elseif ($Apk) {
    if (-not (Test-Path $Apk)) { throw "APK が見つかりません: $Apk" }
    $apkPath = (Resolve-Path $Apk).Path

    # Debug ビルドの APK は Fast Deployment で、アセンブリを APK に入れず別経路で送ることがある。
    # そういう APK を adb install すると、成功して versionCode まで上がるのにコードは古いまま動く
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($apkPath)
    try {
        $hasCode = [bool]($zip.Entries | Where-Object {
            $_.FullName -match '^lib/[^/]+/(libassembly-store\.so|lib_andEmuera\.Android\.dll\.so)$' -or
            $_.FullName -match '^assemblies/'
        } | Select-Object -First 1)
    }
    finally { $zip.Dispose() }
    if (-not $hasCode) {
        throw @"
この APK にはアプリのコードが入っていません (Debug ビルドの Fast Deployment)。
adb install しても古いコードのまま動きます。-Build を使うか、tools\pack.ps1 で作った APK を指定してください。
"@
    }

    Write-Host ""
    Write-Host ("APK を入れます: {0} ({1:N1} MB)" -f $apkPath, ((Get-Item $apkPath).Length / 1MB)) -ForegroundColor Cyan
    if ($PSCmdlet.ShouldProcess($Serial, "adb install -r $apkPath")) {
        # -r は上書き。署名が違えば INSTALL_FAILED_UPDATE_INCOMPATIBLE で止まり、端末のデータには触れない
        $ErrorActionPreference = 'Continue'
        $out = & $adbExe -s $Serial install -r $apkPath 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        $text = $out -join "`n"
        if ($text -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
            throw @"
端末のアプリと署名が違うため上書きできませんでした (端末のデータはそのままです)。
入れ替えるにはアンインストールが必要で、そのとき files/ ごとゲームとセーブが消えます。
必要なら先に sav/ を adb pull で控えてから、手でアンインストールしてください:
    adb uninstall $appId
"@
        }
        if ($text -match 'INSTALL_FAILED_VERSION_DOWNGRADE') {
            throw "端末のほうが新しい版です (versionCode $($installed.VersionCode))。古い APK は入れられません。"
        }
        if ($code -ne 0 -or $text -notmatch 'Success') { throw "インストールに失敗しました:`n$text" }
        $installed = Get-InstalledInfo
        Write-Host "入れました: $($installed.VersionName) (versionCode $($installed.VersionCode))" -ForegroundColor Green
    }
}

if (-not $installed -and -not $WhatIfPreference) {
    Write-Host "アプリがまだ入っていません。-Apk か -Build で入れてください (ゲームは先に送れます)。" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 2. ゲーム

# ローカルのファイル一覧。相対パス (/ 区切り) → @{ Size; Time (UNIX 秒) }
# DirectoryInfo.EnumerateFiles は FindFirstFile の結果からサイズと時刻を埋めるので、ファイルごとの stat が要らない
# 端末は大文字小文字を区別するので、突き合わせも区別する (@{} は区別しない)
function New-Index { ,[Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal) }

function Get-LocalIndex {
    param([string]$Root)
    $index = New-Index
    $rootInfo = [IO.DirectoryInfo]::new($Root)
    $prefix = $rootInfo.FullName.TrimEnd('\').Length + 1
    foreach ($f in $rootInfo.EnumerateFiles('*', [IO.SearchOption]::AllDirectories)) {
        $rel = $f.FullName.Substring($prefix).Replace('\', '/')
        $index[$rel] = [pscustomobject]@{
            Size = $f.Length
            Time = [DateTimeOffset]::new($f.LastWriteTimeUtc).ToUnixTimeSeconds()
            Full = $f.FullName
        }
    }
    return $index
}

# 端末側の一覧。toybox find の -printf で "サイズ 時刻 相対パス" を出す (%T@ は小数付きの UNIX 秒)。
# -exec stat {} + は日本語の長いパスが続くと Argument list too long で落ちるので使わない
function Get-RemoteIndex {
    param([string]$RemoteDir)
    $q = Quote-Sh $RemoteDir
    $out = Invoke-Remote "cd $q && find . -type f -printf '%s %T@ %P\n'"
    $index = New-Index
    foreach ($line in $out -split "`n") {
        if ($line -match '^(\d+) (\d+)(?:\.\d*)? (.+)$') {
            $index[$Matches[3]] = [pscustomobject]@{ Size = [long]$Matches[1]; Time = [long]$Matches[2] }
        }
    }
    return $index
}

function Test-RemoteDir([string]$Path) {
    (Invoke-Remote "if [ -d $(Quote-Sh $Path) ]; then echo yes; else echo no; fi").Trim() -eq 'yes'
}

# 端末の sav/ を PC へ控える。adb pull の local 側に日本語を渡さないよう、一時名で受けてから移す
function Backup-RemoteSave {
    param([string]$GameName)
    $remoteSav = "$remoteGames/$GameName/sav"
    if (-not (Test-RemoteDir $remoteSav)) { return }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dest = Join-Path (Join-Path $BackupDir $GameName) $stamp
    $tmp = Join-Path $env:TEMP "andemuera-sav-$stamp"
    $tmpRemote = "$remoteGames/zz_andemuera_sav"
    # pull も remote 側が日本語のディレクトリだと怪しいので、ASCII 名へ複製してから引く
    Invoke-Remote "rm -rf $(Quote-Sh $tmpRemote) && cp -rp $(Quote-Sh $remoteSav) $(Quote-Sh $tmpRemote)" | Out-Null
    try {
        Invoke-Adb pull $tmpRemote $tmp | Out-Null
    }
    finally {
        Invoke-Remote "rm -rf $(Quote-Sh $tmpRemote)" | Out-Null
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Move-Item $tmp $dest
    Write-Host "  端末の sav/ を控えました: $dest"
}

function Send-Game {
    param([string]$Path)

    $root = (Resolve-Path $Path).Path.TrimEnd('\')
    $name = Split-Path -Leaf $root
    $hasCsv = (Test-Path (Join-Path $root 'csv')) -or (Test-Path (Join-Path $root 'CSV'))
    $hasErb = (Test-Path (Join-Path $root 'erb')) -or (Test-Path (Join-Path $root 'ERB'))
    if (-not ($hasCsv -and $hasErb)) {
        throw "csv と erb を含むフォルダではありません: $root"
    }
    if ($name -match "[`n/]") { throw "フォルダ名に使えない文字があります: $name" }

    Write-Host ""
    Write-Host "ゲーム: $name" -ForegroundColor Cyan
    $remoteDir = "$remoteGames/$name"
    $stageRemote = "$remoteGames/zz_andemuera_stage"

    Write-Host "  PC 側の一覧を取っています…"
    $local = Get-LocalIndex $root
    $localBytes = ($local.Values | Measure-Object Size -Sum).Sum
    Write-Host ("  PC: {0:N0} ファイル / {1:N1} MB" -f $local.Count, ($localBytes / 1MB))

    Invoke-Remote "mkdir -p $(Quote-Sh $remoteGames)" | Out-Null
    $exists = Test-RemoteDir $remoteDir

    if (-not $exists) {
        # 初回はフォルダごと。sav/ もそのまま送る (端末に上書きされるセーブが無いので)
        Write-Host "  端末に無いので、フォルダごと送ります。"
        if (-not $PSCmdlet.ShouldProcess("$Serial : $remoteDir", "フォルダごと送る ($($local.Count) ファイル)")) { return }
        Invoke-Remote "rm -rf $(Quote-Sh $stageRemote)" | Out-Null
        $sw = [Diagnostics.Stopwatch]::StartNew()
        # ここは adb の進捗表示をそのまま見せる (大きいゲームは数分かかる)
        Invoke-AdbPush $root $stageRemote
        Invoke-Remote "mv $(Quote-Sh $stageRemote) $(Quote-Sh $remoteDir)" | Out-Null
        Write-Host ("  送りました ({0:N0} 秒)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
        return
    }

    Write-Host "  端末側の一覧を取っています…"
    $remote = Get-RemoteIndex $remoteDir

    # 大文字小文字だけ違う名前が端末にあれば、PC の綴りに改名する (PC を正とする)。
    # PC の CSV/ が端末では csv/ になっていることがある。突き合わせは端末の今の綴りで行い、
    # 送る前に端末側を改名してから、PC の綴りのまま送る
    $remoteCi = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($p in $remote.Keys) {
        $parts = $p -split '/'
        for ($i = 1; $i -le $parts.Count; $i++) {
            $prefix = $parts[0..($i - 1)] -join '/'
            if (-not $remoteCi.ContainsKey($prefix)) { $remoteCi[$prefix] = $prefix }
        }
    }
    $target = @{}   # PC の相対パス → 端末での今の相対パス
    # 改名: PC の綴りでの新しいパス → 改名前の名前 (親はすでに PC の綴りに直っている前提)。
    # 浅いほうから順に mv すれば、深いほうの「親」は直ったあとの綴りになる
    $renames = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($rel in $local.Keys) {
        $t = $rel
        if (-not $remote.ContainsKey($rel)) {
            $parts = $rel -split '/'
            $acc = ''
            for ($i = 0; $i -lt $parts.Count; $i++) {
                $pcPath = $parts[0..$i] -join '/'
                $acc = if ($i -eq 0) { $parts[0] } else { "$acc/$($parts[$i])" }
                if ($remoteCi.ContainsKey($acc)) {
                    $acc = $remoteCi[$acc]
                    $remoteLeaf = ($acc -split '/')[-1]
                    if ($remoteLeaf -cne $parts[$i]) { $renames[$pcPath] = $remoteLeaf }
                }
                else {
                    # ここから先は端末に無い。残りは PC の綴りのまま
                    if ($i + 1 -lt $parts.Count) { $acc = "$acc/" + ($parts[($i + 1)..($parts.Count - 1)] -join '/') }
                    break
                }
            }
            $t = $acc
        }
        $target[$rel] = $t
    }
    # 浅い順。改名前のパスは「PC の綴りの親 / 端末の今の名前」
    $renameOps = @($renames.Keys | Sort-Object { ($_ -split '/').Count }, { $_ } | ForEach-Object {
        $slash = $_.LastIndexOf('/')
        $old = if ($slash -lt 0) { $renames[$_] } else { $_.Substring(0, $slash + 1) + $renames[$_] }
        [pscustomobject]@{ Old = $old; New = $_ }
    })
    if ($renameOps.Count) {
        Write-Host "  端末側の $($renameOps.Count) 件の名前を PC の大文字小文字に合わせます"
        $renameOps | Select-Object -First 10 | ForEach-Object { Write-Host "    $($_.Old) → $($_.New)" }
        if ($renameOps.Count -gt 10) { Write-Host "    …" }
    }

    $savePattern = '^(?i)sav/'
    $added = [Collections.Generic.List[string]]::new()
    $changed = [Collections.Generic.List[string]]::new()
    $skippedSave = 0
    foreach ($rel in $local.Keys) {
        $l = $local[$rel]
        $t = $target[$rel]
        $r = if ($remote.ContainsKey($t)) { $remote[$t] } else { $null }
        # 時刻は「PC のほうが新しい」ときだけ差とみなす。以前 cp (-p なし) で入れたものは
        # 端末側の時刻が入れた日時になっているので、一致を求めると全部送り直しになる
        $differs = (-not $r) -or ($r.Size -ne $l.Size) -or ($l.Time -gt $r.Time)
        if (-not $differs) { continue }
        if ($rel -match $savePattern -and -not $WithSave) { $skippedSave++; continue }
        if ($r) { $changed.Add($rel) } else { $added.Add($rel) }
    }
    $targets = [Collections.Generic.HashSet[string]]::new([string[]]$target.Values, [StringComparer]::Ordinal)
    $onlyRemote = @($remote.Keys | Where-Object { -not $targets.Contains($_) -and $_ -notmatch $savePattern }).Count

    $send = @(@($added) + @($changed) | Sort-Object)
    $sendBytes = ($send | ForEach-Object { $local[$_].Size } | Measure-Object -Sum).Sum
    Write-Host ("  新規 {0:N0} / 変更 {1:N0} ファイル ({2:N1} MB)" -f $added.Count, $changed.Count, ($sendBytes / 1MB))
    if ($skippedSave) {
        Write-Host "  sav/ の $skippedSave ファイルは送りません (端末のセーブを守るため。送るなら -WithSave)" -ForegroundColor Yellow
    }
    if ($onlyRemote) {
        Write-Host "  端末にだけあるファイルが $onlyRemote 個あります (消しません)"
    }
    if ($send.Count -eq 0 -and $renameOps.Count -eq 0) {
        Write-Host "  端末は最新です。" -ForegroundColor Green
        return
    }
    if ($send.Count -le 20) { $send | ForEach-Object { Write-Host "    $_" } }

    if (-not $PSCmdlet.ShouldProcess("$Serial : $remoteDir", "$($send.Count) ファイルを送る")) { return }

    if ($renameOps.Count) {
        # 端末のストレージは大文字小文字を区別しないことがあり、mv csv CSV は「同じもの」で失敗しうる。
        # いったん別名を挟む
        $lines = foreach ($op in $renameOps) {
            $o = Quote-Sh "$remoteDir/$($op.Old)"
            $tmp = Quote-Sh "$remoteDir/$($op.Old).zz_recase"
            $n = Quote-Sh "$remoteDir/$($op.New)"
            "mv $o $tmp && mv $tmp $n || exit 1"
        }
        Invoke-Remote ($lines -join "`n") | Out-Null
        Write-Host "  名前を合わせました。"
    }
    if ($send.Count -eq 0) { return }

    if ($WithSave -and ($send -match $savePattern)) { Backup-RemoteSave $name }

    # 差分を ASCII 名の一時フォルダに構造ごと複製し、ディレクトリ 1 個として送ってから合流させる。
    # File.Copy は更新時刻を保つので、次回の突き合わせでも一致する
    $stage = Join-Path $env:TEMP "andemuera-stage-$PID"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    try {
        foreach ($rel in $send) {
            # 端末側はもう PC の綴りに直っているので、PC の相対パスのまま置く
            $dst = Join-Path $stage $rel.Replace('/', '\')
            [IO.Directory]::CreateDirectory((Split-Path $dst)) | Out-Null
            [IO.File]::Copy($local[$rel].Full, $dst)
        }
        Invoke-Remote "rm -rf $(Quote-Sh $stageRemote)" | Out-Null
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Invoke-AdbPush $stage $stageRemote
        # -p で更新時刻を保つ (落とすと次回すべて「変更」に見える)
        Invoke-Remote "cp -rfp $(Quote-Sh "$stageRemote/.") $(Quote-Sh "$remoteDir/") && rm -rf $(Quote-Sh $stageRemote)" | Out-Null
        Write-Host ("  送りました ({0:N0} 秒)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    }
    finally {
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    }
}

foreach ($g in $Game) { Send-Game $g }

# ---------------------------------------------------------------- 3. フォント

if ($Font) {
    Write-Host ""
    Write-Host "フォント" -ForegroundColor Cyan
    Invoke-Remote "mkdir -p $(Quote-Sh "$remoteFiles/fonts")" | Out-Null
    foreach ($f in $Font) {
        if (-not (Test-Path $f)) { throw "フォントが見つかりません: $f" }
        $item = Get-Item $f
        if ($item.Extension -notin '.ttf', '.otf', '.ttc') { throw "ttf / otf / ttc ではありません: $f" }
        if ($PSCmdlet.ShouldProcess("$Serial : $remoteFiles/fonts/", "$($item.Name) を送る")) {
            # ファイル 1 個 + remote のフルパスなら日本語名でも正しく届く
            Invoke-Adb push $item.FullName "$remoteFiles/fonts/$($item.Name)" | Out-Null
            Write-Host "  $($item.Name)"
        }
    }
}

# ---------------------------------------------------------------- 後始末と起動

Invoke-Adb shell "rm -f $remoteScript" | Out-Null

if ($Launch -and $installed) {
    if ($PSCmdlet.ShouldProcess($Serial, "$appId を起動")) {
        # 送ったゲームを読み直させるため、いったん止めてから起動する
        Invoke-Adb shell am force-stop $appId | Out-Null
        Invoke-Adb shell monkey -p $appId -c android.intent.category.LAUNCHER 1 | Out-Null
        Write-Host ""
        Write-Host "起動しました。" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "完了" -ForegroundColor Green
