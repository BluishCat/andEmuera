<#
.SYNOPSIS
    andEmuera のリリース署名鍵 (keystore) を作る。最初の 1 回だけ実行する。

.DESCRIPTION
    Android は「同じ鍵で署名された APK でないと上書きインストールできない」。
    デバッグビルドの鍵は環境ごとに変わるため、配布物はここで作る鍵で署名する。

    この鍵を失うと、配布済みのアプリに更新版を届けられなくなる (利用者が
    アンインストールしてセーブごと消すしかなくなる)。**必ずバックアップを取ること。**

    パスワードはこのスクリプトに渡さない。keytool が対話で聞いてくるので、そこで入力する。
    keystore もパスワードもリポジトリには置かない。

.EXAMPLE
    .\tools\make-keystore.ps1

    作成後、ビルド時に読ませるパスワードを環境変数へ入れる:
        $env:ANDEMUERA_KEYSTORE_PASS = '<入力したパスワード>'
        .\tools\pack.ps1
#>
[CmdletBinding()]
param(
    # 鍵の置き場。リポジトリの外に置く
    [string]$KeyStore = (Join-Path $env:USERPROFILE '.andemuera\andemuera-release.keystore'),
    [string]$Alias = 'andemuera',
    # 証明書の識別名。サイドロード配布では表示されないので既定のままでよい
    [string]$Dname = 'CN=andEmuera, OU=andEmuera, O=andEmuera',
    # 有効期限 (日)。既定は 30 年
    [int]$ValidityDays = 10950
)

$ErrorActionPreference = 'Stop'

function Find-Keytool {
    $cmd = Get-Command keytool -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += (Join-Path $env:JAVA_HOME 'bin\keytool.exe') }
    $candidates += @(
        "$env:ProgramFiles\Java\*\bin\keytool.exe",
        "$env:ProgramFiles\Microsoft\jdk-*\bin\keytool.exe",
        "$env:ProgramFiles\Android\jdk\*\bin\keytool.exe",
        # Visual Studio の Android ワークロードはこちらに入れる
        "$env:ProgramFiles\Android\openjdk\*\bin\keytool.exe",
        "$env:ProgramFiles\Eclipse Adoptium\*\bin\keytool.exe",
        "$env:LOCALAPPDATA\Android\Sdk\jdk\*\bin\keytool.exe"
    )

    foreach ($pattern in $candidates) {
        $found = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$keytool = Find-Keytool
if (-not $keytool) {
    throw "keytool が見つかりません。JDK を入れるか、JAVA_HOME を設定してください。"
}

if (Test-Path $KeyStore) {
    Write-Host "鍵はすでにあります: $KeyStore" -ForegroundColor Yellow
    Write-Host "作り直すと、この鍵で署名した APK へ上書きインストールできなくなります。"
    Write-Host "本当に作り直すなら、先に既存ファイルを別名で退避してください。"
    exit 1
}

$dir = Split-Path -Parent $KeyStore
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

Write-Host "keytool: $keytool"
Write-Host "作成先 : $KeyStore"
Write-Host ""
Write-Host "パスワードを聞かれます。控えを残してください (このあと環境変数に入れます)。" -ForegroundColor Cyan
Write-Host ""

# PKCS12 は keystore と鍵のパスワードを分けられないので、1 つで通す
& $keytool -genkeypair -v `
    -keystore $KeyStore `
    -alias $Alias `
    -keyalg RSA -keysize 4096 `
    -validity $ValidityDays `
    -storetype PKCS12 `
    -dname $Dname

if ($LASTEXITCODE -ne 0) {
    throw "keytool が失敗しました (終了コード $LASTEXITCODE)。"
}

Write-Host ""
Write-Host "作成しました: $KeyStore" -ForegroundColor Green
Write-Host ""
Write-Host "次にやること:" -ForegroundColor Cyan
Write-Host "  1. $KeyStore を別の場所へバックアップする (失うと更新版を配れません)"
Write-Host "  2. 配布物を作るときにパスワードを環境変数で渡す:"
Write-Host "       `$env:ANDEMUERA_KEYSTORE_PASS = '<入力したパスワード>'"
Write-Host "       .\tools\pack.ps1"
